namespace DinoTranslator.Desktop.Services.Audio;

/// <summary>
/// Supressão de ruído do microfone, como guardada no WebView2 do app (noise.js). Os mesmos
/// limites valem dos dois lados: o que vem do JavaScript é conferido de novo aqui.
/// </summary>
public sealed record NoiseSettings(
    string Mode,
    int Intensity,
    bool AutoSensitivity,
    int SensitivityDb,
    bool EchoCancellation,
    bool AutoGain)
{
    public const int MinSensitivityDb = -80;
    public const int MaxSensitivityDb = -10;

    public static NoiseSettings Default { get; } = new(NoiseModes.DeepFilter, 60, true, -50, true, true);

    public NoiseSettings Sanitized() => new(
        NoiseModes.IsKnown(Mode) ? Mode : Default.Mode,
        Math.Clamp(Intensity, 0, 100),
        AutoSensitivity,
        Math.Clamp(SensitivityDb, MinSensitivityDb, MaxSensitivityDb),
        EchoCancellation,
        AutoGain);
}

/// <summary>O que está rodando de fato: o modo pode ter descido para um mais leve.</summary>
public sealed record NoiseState(NoiseSettings Settings, string? Mode, string? Problem)
{
    public NoiseState Sanitized() => new(
        Settings.Sanitized(),
        NoiseModes.IsKnown(Mode) ? Mode : null,
        Problem is NoiseModes.Overload or NoiseModes.Unavailable ? Problem : null);

    /// <summary>Frase curta para o painel: o que está ativo e por quê, se não for o escolhido.</summary>
    public string Describe()
    {
        if (Mode is null)
        {
            return "Liga junto com o microfone.";
        }
        var running = NoiseModes.Get(Mode).Name;
        return Problem switch
        {
            NoiseModes.Overload => $"O {NoiseModes.Get(Settings.Mode).Name} pesou demais neste computador; usando o {running}.",
            NoiseModes.Unavailable when Mode != Settings.Mode => $"O {NoiseModes.Get(Settings.Mode).Name} não carregou; usando: {running}.",
            _ => $"Ativo: {running}.",
        };
    }
}

public static class NoiseModes
{
    public const string Off = "desligado";
    public const string Browser = "navegador";
    public const string Rnnoise = "rnnoise";
    public const string Gtcrn = "gtcrn";
    public const string DeepFilter = "deepfilter";

    public const string Overload = "overload";
    public const string Unavailable = "unavailable";

    public sealed record Option(string Id, string Name, string Description);

    /// <summary>Do mais leve ao mais forte.</summary>
    public static IReadOnlyList<Option> All { get; } =
    [
        new(Off, "Desligada", "O microfone vai como está. Para quem grava com microfone bom num lugar silencioso."),
        new(Browser, "Padrão", "A do próprio WebRTC, a de antes. Leve; tira chiado constante."),
        new(Rnnoise, "RNNoise", "Leve. Tira ventilador, ar-condicionado e chiado."),
        new(Gtcrn, "GTCRN", "Equilibrada. Tira também barulho de rua, cozinha e conversa ao fundo."),
        new(DeepFilter, "DeepFilterNet3", "Máxima. Tira teclado, cachorro, obra e música ao fundo, mantendo a voz cheia."),
    ];

    public static bool IsKnown(string? id) => All.Any(o => o.Id == id);

    public static Option Get(string? id) => All.FirstOrDefault(o => o.Id == id) ?? All[^1];
}
