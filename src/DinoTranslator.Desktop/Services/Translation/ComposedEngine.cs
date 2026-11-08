using System.Text.Json.Nodes;
using DinoTranslator.Desktop.Services.Translation.Extras;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Services.Translation;

/// <summary>Reconhecimento de uma pessoa (idioma fixo): versão provisória ou final de um trecho.</summary>
public interface IRecognitionPart : IAsyncDisposable
{
    Task<string> TranscribeAsync(float[] samples, bool final, CancellationToken cancellationToken);
}

/// <summary>Tradução de texto, rápida (provisórias) ou cuidadosa (final e chat).</summary>
public interface ITranslationPart
{
    Task<string> TranslateAsync(string text, string source, string target, bool careful, CancellationToken cancellationToken);
}

/// <summary>whisper.cpp (Vulkan): provisória rápida, final com busca em feixe se a combinação pedir.</summary>
internal sealed class WhisperCppPart(WhisperCppEngine engine, SpeechRecognizer recognizer, bool careful) : IRecognitionPart
{
    public Task<string> TranscribeAsync(float[] samples, bool final, CancellationToken cancellationToken) =>
        engine.TranscribeAsync(recognizer, samples, final && careful, cancellationToken);

    public ValueTask DisposeAsync() => recognizer.DisposeAsync();
}

/// <summary>faster-whisper no motor auxiliar: o áudio vai em PCM 16 bits.</summary>
internal sealed class FasterWhisperPart(PythonEngine python, string key, string language, string? prompt, bool careful) : IRecognitionPart
{
    public async Task<string> TranscribeAsync(float[] samples, bool final, CancellationToken cancellationToken)
    {
        var padded = WhisperCppEngine.PadToMinimum(samples);
        var pcm = new byte[padded.Length * 2];
        for (var i = 0; i < padded.Length; i++)
        {
            var value = (short)Math.Clamp(padded[i] * 32768f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)value;
            pcm[(i * 2) + 1] = (byte)(value >> 8);
        }
        var answer = await python.RunAsync("asr", key, new JsonObject
        {
            ["language"] = WhisperCppEngine.WhisperLanguage(language),
            ["audio"] = Convert.ToBase64String(pcm),
            ["beam"] = final && careful ? WhisperCppEngine.FinalBeamSize : 1,
            ["prompt"] = prompt,
        }, cancellationToken);
        return WhisperCppEngine.RemoveArtifacts(answer["text"]?.GetValue<string>() ?? string.Empty);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>NLLB-200 600M em ONNX, no próprio app (o tradutor original).</summary>
internal sealed class NllbOnnxPart(NllbTranslator translator) : ITranslationPart
{
    public Task<string> TranslateAsync(string text, string source, string target, bool careful, CancellationToken cancellationToken) =>
        Task.Run(() => translator.Translate(text, source, target, cancellationToken, careful ? NllbTranslator.CarefulBeamWidth : 1), cancellationToken);
}

/// <summary>
/// CTranslate2 no motor auxiliar (NLLB ou OPUS-MT). Par sem modelo OPUS-MT usa o tradutor de
/// reserva (o NLLB original), se houver.
/// </summary>
internal sealed class Ct2Part(
    PythonEngine python,
    Func<string, string, CancellationToken, Task<string?>> modelFor,
    ITranslationPart? fallback) : ITranslationPart
{
    public async Task<string> TranslateAsync(string text, string source, string target, bool careful, CancellationToken cancellationToken)
    {
        if (await modelFor(source, target, cancellationToken) is not { } key)
        {
            return fallback is not null
                ? await fallback.TranslateAsync(text, source, target, careful, cancellationToken)
                : throw new PythonEngineException("Não há modelo para este par de idiomas.");
        }
        var answer = await python.RunAsync("mt", key, new JsonObject
        {
            ["text"] = text,
            ["source"] = source,
            ["target"] = target,
            ["beam"] = careful ? NllbTranslator.CarefulBeamWidth : 1,
        }, cancellationToken);
        return answer["text"]?.GetValue<string>() ?? string.Empty;
    }
}

/// <summary>
/// Motor local montado com as peças escolhidas: detector de voz, pausa, reconhecimento, tradução e
/// versão final. A combinação padrão é o motor original do app.
/// </summary>
public sealed class ComposedSpeechTranslationEngine(SpeechEngineFactory parts, TranslationSettings settings, EngineChoice choice) : ISpeechTranslationEngine
{
    public EngineChoice Choice => choice;

    public bool CanTranslateText => true;

    public async Task<ISpeechTranslationStream> StartAsync(SpeechStreamOptions options, CancellationToken cancellationToken)
    {
        var recognition = await parts.RecognitionAsync(settings, choice, options.SourceLanguage, options.Phrases, cancellationToken);
        try
        {
            var translation = Languages.NeedsTranslation(options.SourceLanguage, options.TargetLanguage)
                ? await parts.TranslationAsync(settings, choice, cancellationToken)
                : null;
            var detector = choice.Detector == DetectorOption.Silero ? new SileroVoiceDetector(await parts.SileroAsync(cancellationToken)) : null;
            await WarmUpAsync(recognition, translation, options, cancellationToken);
            return new Stream(recognition, translation, detector, options, choice);
        }
        catch
        {
            await recognition.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// O primeiro uso de cada modelo é lento (a placa compila os kernels, o OPUS-MT carrega o modelo
    /// do par): isso acontece aqui, enquanto o painel mostra "carregando", e não na primeira frase.
    /// </summary>
    private static async Task WarmUpAsync(IRecognitionPart recognition, ITranslationPart? translation, SpeechStreamOptions options, CancellationToken cancellationToken)
    {
        await recognition.TranscribeAsync(new float[SpeechSegmenter.SampleRate], final: false, cancellationToken);
        await recognition.TranscribeAsync(new float[SpeechSegmenter.SampleRate], final: true, cancellationToken);
        if (translation is not null)
        {
            await translation.TranslateAsync("Olá.", options.SourceLanguage, options.TargetLanguage, careful: false, cancellationToken);
            await translation.TranslateAsync("Olá.", options.SourceLanguage, options.TargetLanguage, careful: true, cancellationToken);
        }
    }

    public async Task<string?> TranslateTextAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        var translation = await parts.TranslationAsync(settings, choice, cancellationToken);
        return await translation.TranslateAsync(text, sourceLanguage, targetLanguage, choice.Careful, cancellationToken);
    }

    /// <summary>Fala de uma pessoa: segmentador, reconhecimento e tradução na linha de montagem.</summary>
    private sealed class Stream : ISpeechTranslationStream
    {
        private readonly IRecognitionPart _recognition;
        private readonly SpeechSegmenter _segmenter;
        private readonly SpeechPipeline _pipeline;

        public Stream(IRecognitionPart recognition, ITranslationPart? translation, IVoiceDetector? detector, SpeechStreamOptions options, EngineChoice choice)
        {
            _recognition = recognition;
            _segmenter = new SpeechSegmenter(detector, choice.PauseMs);
            var translate = translation is null
                ? (Func<string, bool, CancellationToken, Task<string>>?)null
                : (text, careful, token) => translation.TranslateAsync(text, options.SourceLanguage, options.TargetLanguage, careful, token);
            _pipeline = new SpeechPipeline(
                (segment, token) => recognition.TranscribeAsync(segment.Samples, segment.Final, token),
                translate,
                carefulFinals: choice.Careful,
                pauseMs: choice.PauseMs);
            _pipeline.Updated += update => Updated?.Invoke(update);
            _pipeline.Failed += () => Failed?.Invoke("O motor local parou de responder. A chamada continua, sem legendas.");
        }

        public event Action<SpeechUpdate>? Updated;

        public event Action<string>? Failed;

        public void Write(byte[] pcm)
        {
            if (_segmenter.Append(pcm) is { } segment)
            {
                _pipeline.Enqueue(segment);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _pipeline.DisposeAsync();
            await _recognition.DisposeAsync();
        }
    }
}
