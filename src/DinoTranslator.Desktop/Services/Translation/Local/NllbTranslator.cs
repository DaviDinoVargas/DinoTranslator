using System.IO;
using DinoTranslator.Shared;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>
/// Identificadores do vocabulário do NLLB-200: as peças do SentencePiece deslocadas em 1
/// (0–3 são &lt;s&gt;, &lt;pad&gt;, &lt;/s&gt; e &lt;unk&gt;) e os códigos de idioma depois do vocabulário.
/// </summary>
public static class NllbVocabulary
{
    public const long EndOfSentence = 2;
    public const long Unknown = 3;
    public const long FirstLanguage = 256_001;
    public const int MaxSourceTokens = 256;

    public static long LanguageId(string language) => language switch
    {
        Languages.PortugueseBrazil => 256_141, // por_Latn
        Languages.English => 256_047,          // eng_Latn
        Languages.Ukrainian => 256_188,        // ukr_Cyrl
        Languages.Russian => 256_147,          // rus_Cyrl
        Languages.Spanish => 256_161,          // spa_Latn
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Idioma não suportado."),
    };

    /// <summary>Peça do SentencePiece → id do modelo; a peça desconhecida (0) vira o &lt;unk&gt; do modelo.</summary>
    public static long ToModelId(int pieceId) => pieceId == 0 ? Unknown : pieceId + 1L;

    public static bool IsText(long modelId) => modelId > Unknown && modelId < FirstLanguage;

    public static int ToPieceId(long modelId) => (int)(modelId - 1);

    /// <summary>Entrada do codificador: [idioma de origem] texto [fim].</summary>
    public static long[] Source(IReadOnlyList<int> pieces, string language)
    {
        var count = Math.Min(pieces.Count, MaxSourceTokens - 2);
        var ids = new long[count + 2];
        ids[0] = LanguageId(language);
        for (var i = 0; i < count; i++)
        {
            ids[i + 1] = ToModelId(pieces[i]);
        }
        ids[^1] = EndOfSentence;
        return ids;
    }

    /// <summary>Repetição em laço (as últimas 4 saídas iguais às 4 anteriores): é hora de parar.</summary>
    public static bool IsLooping(IReadOnlyList<long> output) =>
        output.Count >= 8 && Enumerable.Range(0, 4).All(i => output[^(i + 1)] == output[^(i + 5)]);
}

/// <summary>
/// Tradução local com o NLLB-200 (600M, quantizado) no ONNX Runtime, na CPU: codificador uma
/// vez por frase e decodificador palavra por palavra (cerca de 45 ms cada), reaproveitando o
/// cache de atenção.
/// </summary>
public sealed class NllbTranslator : IDisposable
{
    private const int MaxNewTokens = 200;
    private const string PastPrefix = "past_key_values";
    private const string PresentPrefix = "present";

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePieceTokenizer _vocabulary;
    private readonly string[] _pastNames;
    private readonly int _heads;
    private readonly int _headSize;
    private readonly Lock _gate = new();

    public NllbTranslator(string encoderPath, string decoderPath, string vocabularyPath, int threads)
    {
        using var options = Options(threads);
        _encoder = new InferenceSession(encoderPath, options);

        // A tabela de 256 mil palavras vem em 8 bits e o ONNX Runtime a descomprimia inteira
        // (cerca de 1 GB) a cada palavra gerada: 155 ms de 200 por passo. Assim ela é
        // descomprimida uma vez, ao carregar, e o passo cai para uns 45 ms.
        using var decoderOptions = Options(threads);
        decoderOptions.AddSessionConfigEntry("session.disable_quant_qdq", "1");
        _decoder = new InferenceSession(decoderPath, decoderOptions);
        using (var stream = File.OpenRead(vocabularyPath))
        {
            _vocabulary = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        }

        _pastNames = _decoder.InputMetadata.Keys.Where(n => n.StartsWith(PastPrefix + ".", StringComparison.Ordinal)).ToArray();
        var shape = _decoder.InputMetadata[_pastNames[0]].Dimensions;
        _heads = shape[1] > 0 ? shape[1] : 16;
        _headSize = shape[3] > 0 ? shape[3] : 64;
    }

    /// <param name="cancellationToken">Conferido a cada palavra: uma tradução provisória pode ser abandonada no meio.</param>
    public string Translate(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        lock (_gate)
        {
            var pieces = _vocabulary.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
            var source = NllbVocabulary.Source(pieces, sourceLanguage);
            var mask = new DenseTensor<long>(Enumerable.Repeat(1L, source.Length).ToArray(), [1, source.Length]);

            using var encoded = _encoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(source, [1, source.Length])),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            ]);
            var hidden = encoded.First(v => v.Name == "last_hidden_state").AsTensor<float>();

            var output = Decode(hidden, mask, NllbVocabulary.LanguageId(targetLanguage), MaxTokens(source.Length), cancellationToken);
            return _vocabulary.Decode(output.Where(NllbVocabulary.IsText).Select(NllbVocabulary.ToPieceId)).Trim();
        }
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }

    /// <summary>
    /// Sem espera ativa: por padrão as threads do ONNX Runtime ficam girando no processador entre
    /// uma operação e outra, e disputavam a CPU com o áudio e o vídeo da chamada.
    /// </summary>
    private static SessionOptions Options(int threads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return options;
    }

    private static int MaxTokens(int sourceLength) => Math.Min(MaxNewTokens, (sourceLength * 3) + 10);

    private List<long> Decode(Tensor<float> hidden, DenseTensor<long> mask, long targetLanguage, int maxTokens, CancellationToken cancellationToken)
    {
        var output = new List<long>();
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? first = null;
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? previous = null;
        try
        {
            // Primeiro passo sem cache: início de frase e o idioma de destino forçado.
            long[] next = [NllbVocabulary.EndOfSentence, targetLanguage];
            for (var step = 0; step < maxTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(next, [1, next.Length])),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                    NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { step > 0 }, [1])),
                };
                foreach (var name in _pastNames)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, step == 0 ? EmptyPast() : Past(name, first!, previous!)));
                }

                var result = _decoder.Run(inputs);
                if (step == 0)
                {
                    first = result;
                }
                else if (previous != first)
                {
                    previous!.Dispose();
                }
                previous = result;

                var token = Best(result.First(v => v.Name == "logits").AsTensor<float>());
                if (token == NllbVocabulary.EndOfSentence)
                {
                    break;
                }
                output.Add(token);
                if (NllbVocabulary.IsLooping(output))
                {
                    break;
                }
                next = [token];
            }
        }
        finally
        {
            if (previous != first)
            {
                previous?.Dispose();
            }
            first?.Dispose();
        }
        return output;
    }

    private DenseTensor<float> EmptyPast() => new(Array.Empty<float>(), [1, _heads, 0, _headSize]);

    /// <summary>Cache do decodificador vem do passo anterior; o do codificador é o do primeiro passo.</summary>
    private static Tensor<float> Past(
        string pastName,
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> first,
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> previous)
    {
        var presentName = PresentPrefix + pastName[PastPrefix.Length..];
        var source = pastName.Contains(".encoder.", StringComparison.Ordinal) ? first : previous;
        return source.First(v => v.Name == presentName).AsTensor<float>();
    }

    /// <summary>Palavra mais provável na última posição, só entre as de texto e o fim de frase.</summary>
    private static long Best(Tensor<float> logits)
    {
        var vocabulary = logits.Dimensions[2];
        var values = logits is DenseTensor<float> dense ? dense.Buffer.Span : logits.ToArray();
        var offset = (logits.Dimensions[1] - 1) * vocabulary;
        var best = NllbVocabulary.EndOfSentence;
        var bestScore = values[offset + (int)NllbVocabulary.EndOfSentence];
        var limit = (int)Math.Min(vocabulary, NllbVocabulary.FirstLanguage);
        for (var id = (int)NllbVocabulary.Unknown + 1; id < limit; id++)
        {
            if (values[offset + id] > bestScore)
            {
                bestScore = values[offset + id];
                best = id;
            }
        }
        return best;
    }
}
