namespace DinoTranslator.Desktop.Services.Audio;

/// <summary>De onde vem o som que a pessoa ajusta na chamada.</summary>
public enum AudioSource
{
    /// <summary>A voz da outra pessoa.</summary>
    Person,

    /// <summary>O som da tela que a outra pessoa compartilha.</summary>
    Screen,
}

/// <summary>Volumes e mudos da chamada neste computador, e se a tela (enviada ou recebida) tem som.</summary>
public sealed record CallAudio(
    int PersonVolume,
    bool PersonMuted,
    int ScreenVolume,
    bool ScreenMuted,
    bool RemoteScreenHasAudio,
    bool SendingScreenAudio,
    bool SharingHasAudio)
{
    public static CallAudio Default { get; } = new(100, false, 100, false, false, true, false);
}
