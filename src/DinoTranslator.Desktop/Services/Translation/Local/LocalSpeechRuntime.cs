using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace DinoTranslator.Desktop.Services.Translation.Local;

/// <summary>
/// Modelos locais carregados uma única vez: o Whisper na placa de vídeo (Vulkan, com a CPU
/// como reserva) e o NLLB na CPU. A placa de vídeo atende um trecho por vez.
/// </summary>
public sealed partial class LocalSpeechRuntime : IDisposable
{
    /// <summary>Caminhos comparados na versão final (o padrão do Whisper).</summary>
    public const int FinalBeamSize = 5;

    /// <summary>
    /// Janela de áudio das versões provisórias: 768 quadros (15,36 s) em vez de 30 s, o que basta para
    /// o trecho mais longo (12 s) e deixa o codificador cerca de duas vezes mais rápido. A versão
    /// final usa a janela inteira, como o modelo foi treinado.
    /// </summary>
    public const int QuickAudioContext = 768;

    private readonly WhisperFactory _whisper;

    private LocalSpeechRuntime(WhisperFactory whisper, NllbTranslator translator, WhisperModel model)
    {
        _whisper = whisper;
        Translator = translator;
        Model = model;
    }

    public WhisperModel Model { get; }

    public NllbTranslator Translator { get; }

    /// <summary>Uma transcrição por vez: duas na mesma placa só disputariam memória.</summary>
    public SemaphoreSlim RecognitionGate { get; } = new(1, 1);

    /// <summary>"Vulkan" ou "Cpu": onde o reconhecimento está rodando.</summary>
    public string Device => RuntimeOptions.LoadedLibrary?.ToString() ?? "desconhecido";

    public static LocalSpeechRuntime Load(ModelFolder folder, WhisperModel model)
    {
        foreach (var file in LocalModels.Required(model))
        {
            folder.Verify(file);
        }

        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        var whisper = WhisperFactory.FromPath(folder.PathOf(LocalModels.Whisper(model)), new WhisperFactoryOptions { UseGpu = true, UseFlashAttention = true });
        try
        {
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var translator = new NllbTranslator(
                folder.PathOf(LocalModels.TranslatorEncoder),
                folder.PathOf(LocalModels.TranslatorDecoder),
                folder.PathOf(LocalModels.TranslatorVocabulary),
                threads);
            return new LocalSpeechRuntime(whisper, translator, model);
        }
        catch
        {
            whisper.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reconhecedores de uma pessoa, com o idioma fixo e o glossário como dica: um rápido (a palavra
    /// mais provável de cada vez) para as versões provisórias e um cuidadoso (compara 5 caminhos
    /// antes de decidir) para a versão final, que é a que fica na conversa.
    /// </summary>
    public SpeechRecognizer CreateRecognizer(string language, IReadOnlyList<string> glossary)
    {
        var quick = Builder(language, glossary).WithAudioContextSize(QuickAudioContext).Build();
        try
        {
            var careful = Builder(language, glossary)
                .WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(FinalBeamSize))
                .Build();
            return new SpeechRecognizer(quick, careful);
        }
        catch
        {
            quick.Dispose();
            throw;
        }
    }

    public async Task<string> TranscribeAsync(SpeechRecognizer recognizer, float[] samples, bool final, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await RecognitionGate.WaitAsync(cancellationToken);
        try
        {
            await foreach (var segment in recognizer.For(final).ProcessAsync(PadToMinimum(samples), cancellationToken))
            {
                text.Append(segment.Text);
            }
        }
        finally
        {
            RecognitionGate.Release();
        }
        return RemoveArtifacts(text.ToString());
    }

    public void Dispose()
    {
        Translator.Dispose();
        _whisper.Dispose();
        RecognitionGate.Dispose();
    }

    public static string WhisperLanguage(string language) => language[..2];

    private WhisperProcessorBuilder Builder(string language, IReadOnlyList<string> glossary)
    {
        var builder = _whisper.CreateBuilder()
            .WithLanguage(WhisperLanguage(language))
            .WithNoContext()
            .WithSingleSegment()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
        return glossary.Count > 0 ? builder.WithPrompt(string.Join(", ", glossary)) : builder;
    }

    /// <summary>
    /// O Whisper às vezes "ouve" créditos de legenda em ruído (vício dos dados de treino);
    /// esses textos nunca são fala de verdade e são descartados.
    /// </summary>
    public static string RemoveArtifacts(string text)
    {
        var trimmed = text.Trim();
        return SubtitleCredits().IsMatch(trimmed) || BracketedNoise().IsMatch(trimmed) ? string.Empty : trimmed;
    }

    /// <summary>O Whisper precisa de pelo menos 1 s de áudio; o resto é completado com silêncio.</summary>
    private static float[] PadToMinimum(float[] samples)
    {
        const int minimum = SpeechSegmenter.SampleRate * 11 / 10;
        if (samples.Length >= minimum)
        {
            return samples;
        }
        var padded = new float[minimum];
        samples.CopyTo(padded, 0);
        return padded;
    }

    [GeneratedRegex(@"amara\.org|субтитр|подписывайтесь|продолжение следует|legendas? (pela|por)|subt[ií]tulos (realizados|por)|subtitles by", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubtitleCredits();

    [GeneratedRegex(@"^[\[\(].*[\]\)]$|^[\s.…♪]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedNoise();
}

/// <summary>Os dois reconhecedores de uma pessoa (veja <see cref="LocalSpeechRuntime.CreateRecognizer"/>).</summary>
public sealed class SpeechRecognizer(WhisperProcessor quick, WhisperProcessor careful) : IAsyncDisposable
{
    public WhisperProcessor For(bool final) => final ? careful : quick;

    public async ValueTask DisposeAsync()
    {
        await quick.DisposeAsync();
        await careful.DisposeAsync();
    }
}
