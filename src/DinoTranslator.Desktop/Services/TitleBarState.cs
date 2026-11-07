namespace DinoTranslator.Desktop.Services;

/// <summary>
/// O que a barra de título (WPF) mostra fora da interface Blazor: o relógio de cada pessoa
/// e o estado da chamada.
/// </summary>
public sealed class TitleBarState
{
    public event Action? Changed;

    public string LocalTimeZone { get; private set; } = ProfileStore.LocalTimeZone();

    public string? PeerName { get; private set; }

    public string? PeerTimeZone { get; private set; }

    public void SetLocal(string timeZone)
    {
        LocalTimeZone = timeZone;
        Changed?.Invoke();
    }

    public void SetPeer(string? name, string? timeZone)
    {
        PeerName = name;
        PeerTimeZone = timeZone;
        Changed?.Invoke();
    }
}
