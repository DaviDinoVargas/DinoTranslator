using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DinoTranslator.Desktop.Services.Translation.Extras;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Services.Translation;

/// <summary>Como a fala é separada do silêncio.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DetectorOption>))]
public enum DetectorOption
{
    /// <summary>Pelo volume, com o limiar acompanhando o ruído do ambiente (o original).</summary>
    Volume,

    /// <summary>Silero VAD: rede neural pequena que distingue fala de ruído.</summary>
    Silero,
}

/// <summary>Quem transforma a fala em texto.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecognizerOption>))]
public enum RecognizerOption
{
    WhisperTurbo,
    WhisperSmall,
    WhisperLarge,

    /// <summary>faster-whisper (CTranslate2) turbo na placa de vídeo, por CUDA.</summary>
    FasterWhisperGpu,

    /// <summary>faster-whisper turbo no processador.</summary>
    FasterWhisperCpu,
}

/// <summary>Quem traduz o texto.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TranslatorOption>))]
public enum TranslatorOption
{
    /// <summary>NLLB-200 600M em ONNX, no processador (o original).</summary>
    Nllb600,
    Nllb600Gpu,
    Nllb13Gpu,
    Nllb13Cpu,

    /// <summary>MarianMT (OPUS-MT): um modelo menor por grupo de idiomas.</summary>
    OpusGpu,
    OpusCpu,
}

/// <summary>
/// Combinação de motores da tradução local, escolhida durante a chamada para comparar. O padrão
/// reproduz o motor original: volume, 0,8 s de pausa, Whisper do ajuste, NLLB 600M e final cuidadosa.
/// </summary>
/// <param name="Careful">Versão final com busca em feixe no reconhecimento e na tradução.</param>
/// <param name="Commercial">
/// Uso pago (versão Plus): só peças com licença que permite venda. O NLLB-200 é CC BY-NC 4.0 (uso
/// não comercial) e não entra, nem como reserva; o MarianMT (OPUS-MT, CC BY 4.0) e o Whisper (MIT) sim.
/// </param>
public sealed record EngineChoice(
    DetectorOption Detector,
    int PauseMs,
    RecognizerOption Recognizer,
    TranslatorOption Translator,
    bool Careful,
    bool Commercial = false)
{
    /// <summary>Tradutores que podem ser usados num serviço pago (licença comercial).</summary>
    public static bool IsCommercial(TranslatorOption option) => option is TranslatorOption.OpusGpu or TranslatorOption.OpusCpu;

    /// <summary>
    /// A combinação para a versão Plus: a preferida da pessoa, com o tradutor trocado pelo MarianMT
    /// (na placa, se houver) e sem reserva no NLLB. Sem o MarianMT instalado, não há tradução local paga.
    /// </summary>
    public static EngineChoice? ForPlus(TranslationSettings settings, EngineAvailability availability, EngineChoice? preferred = null)
    {
        var translator = availability.Translators.Contains(TranslatorOption.OpusGpu) ? TranslatorOption.OpusGpu
            : availability.Translators.Contains(TranslatorOption.OpusCpu) ? TranslatorOption.OpusCpu
            : (TranslatorOption?)null;
        if (translator is null)
        {
            return null;
        }
        var basis = preferred ?? For(settings);
        var recognizer = availability.Recognizers.Contains(basis.Recognizer) ? basis.Recognizer : For(settings).Recognizer;
        var detector = availability.Detectors.Contains(basis.Detector) ? basis.Detector : DetectorOption.Volume;
        return new EngineChoice(detector, basis.PauseMs, recognizer, translator.Value, basis.Careful, Commercial: true);
    }

    public static IReadOnlyList<int> Pauses { get; } = [500, 800, 1_200];

    public static EngineChoice For(TranslationSettings settings) => new(
        DetectorOption.Volume,
        SpeechSegmenter.PauseToCloseMs,
        settings.LocalModel switch
        {
            WhisperModel.Small => RecognizerOption.WhisperSmall,
            WhisperModel.Large => RecognizerOption.WhisperLarge,
            _ => RecognizerOption.WhisperTurbo,
        },
        TranslatorOption.Nllb600,
        Careful: true);

    public bool UsesWhisperCpp => Recognizer is RecognizerOption.WhisperTurbo or RecognizerOption.WhisperSmall or RecognizerOption.WhisperLarge;

    public WhisperModel WhisperCppModel => Recognizer switch
    {
        RecognizerOption.WhisperSmall => WhisperModel.Small,
        RecognizerOption.WhisperLarge => WhisperModel.Large,
        _ => WhisperModel.Turbo,
    };

    /// <summary>Choice vinda do disco: valores fora da lista voltam ao padrão.</summary>
    public EngineChoice Sanitized(TranslationSettings settings)
    {
        var fallback = For(settings);
        return new EngineChoice(
            Enum.IsDefined(Detector) ? Detector : fallback.Detector,
            Pauses.Contains(PauseMs) ? PauseMs : fallback.PauseMs,
            Enum.IsDefined(Recognizer) ? Recognizer : fallback.Recognizer,
            Enum.IsDefined(Translator) ? Translator : fallback.Translator,
            Careful,
            Commercial);
    }

    public static string Label(DetectorOption option) => option switch
    {
        DetectorOption.Silero => "Silero (rede neural)",
        _ => "Volume (original)",
    };

    public static string Label(RecognizerOption option) => option switch
    {
        RecognizerOption.WhisperSmall => "Whisper small · placa (Vulkan)",
        RecognizerOption.WhisperLarge => "Whisper large-v3 · placa (Vulkan)",
        RecognizerOption.FasterWhisperGpu => "faster-whisper turbo · placa (CUDA)",
        RecognizerOption.FasterWhisperCpu => "faster-whisper turbo · processador",
        _ => "Whisper turbo · placa (Vulkan)",
    };

    public static string Label(TranslatorOption option) => option switch
    {
        TranslatorOption.Nllb600Gpu => "NLLB 600M · placa (CUDA)",
        TranslatorOption.Nllb13Gpu => "NLLB 1.3B · placa (CUDA)",
        TranslatorOption.Nllb13Cpu => "NLLB 1.3B · processador",
        TranslatorOption.OpusGpu => "MarianMT (OPUS-MT) · placa (CUDA)",
        TranslatorOption.OpusCpu => "MarianMT (OPUS-MT) · processador",
        _ => "NLLB 600M · processador (original)",
    };
}

/// <summary>A última combinação usada, para a próxima chamada começar igual (não é segredo: JSON simples).</summary>
public sealed class EngineChoiceStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EngineChoice? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<EngineChoice>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(EngineChoice choice)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(choice, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem guardar: a próxima chamada começa no padrão.
        }
    }
}

/// <summary>O que dá para escolher neste computador agora (modelos e componentes instalados).</summary>
public sealed record EngineAvailability(
    IReadOnlySet<DetectorOption> Detectors,
    IReadOnlySet<RecognizerOption> Recognizers,
    IReadOnlySet<TranslatorOption> Translators)
{
    public static EngineAvailability Check(IModelFiles models, ExtrasFolder extras)
    {
        var detectors = new HashSet<DetectorOption> { DetectorOption.Volume };
        if (extras.HasSilero)
        {
            detectors.Add(DetectorOption.Silero);
        }

        var recognizers = new HashSet<RecognizerOption>();
        foreach (var (option, model) in new[] { (RecognizerOption.WhisperTurbo, WhisperModel.Turbo), (RecognizerOption.WhisperSmall, WhisperModel.Small), (RecognizerOption.WhisperLarge, WhisperModel.Large) })
        {
            if (models.IsInstalled(LocalModels.Whisper(model)))
            {
                recognizers.Add(option);
            }
        }
        if (extras.HasPython && extras.HasModel(extras.FasterWhisperTurbo))
        {
            recognizers.Add(RecognizerOption.FasterWhisperCpu);
            if (extras.HasCuda)
            {
                recognizers.Add(RecognizerOption.FasterWhisperGpu);
            }
        }

        var translators = new HashSet<TranslatorOption>();
        if (LocalModels.Translator.All(models.IsInstalled))
        {
            translators.Add(TranslatorOption.Nllb600);
        }
        if (extras.HasPython)
        {
            if (extras.HasCuda && extras.HasModel(extras.Ct2("nllb-600m")))
            {
                translators.Add(TranslatorOption.Nllb600Gpu);
            }
            if (extras.HasModel(extras.Ct2("nllb-1.3b")))
            {
                translators.Add(TranslatorOption.Nllb13Cpu);
                if (extras.HasCuda)
                {
                    translators.Add(TranslatorOption.Nllb13Gpu);
                }
            }
            if (OpusModels.All.Any(name => extras.HasModel(extras.Ct2(name))))
            {
                translators.Add(TranslatorOption.OpusCpu);
                if (extras.HasCuda)
                {
                    translators.Add(TranslatorOption.OpusGpu);
                }
            }
        }
        return new EngineAvailability(detectors, recognizers, translators);
    }
}
