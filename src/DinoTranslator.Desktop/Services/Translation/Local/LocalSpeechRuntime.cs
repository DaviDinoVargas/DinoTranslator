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
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        var whisper = WhisperFactory.FromPath(folder.PathOf(LocalModels.Whisper(model)), new WhisperFactoryOptions { UseGpu = true });
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

    /// <summary>Um reconhecedor por pessoa, com o idioma fixo e o glossário como dica.</summary>
    public WhisperProcessor CreateRecognizer(string language, IReadOnlyList<string> glossary)
    {
        var builder = _whisper.CreateBuilder()
            .WithLanguage(WhisperLanguage(language))
            .WithNoContext()
            .WithSingleSegment()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
        if (glossary.Count > 0)
        {
            builder = builder.WithPrompt(string.Join(", ", glossary));
        }
        return builder.Build();
    }

    public async Task<string> TranscribeAsync(WhisperProcessor recognizer, float[] samples, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await RecognitionGate.WaitAsync(cancellationToken);
        try
        {
            await foreach (var segment in recognizer.ProcessAsync(PadToMinimum(samples), cancellationToken))
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
