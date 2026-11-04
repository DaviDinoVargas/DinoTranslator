using DinoTranslator.Shared.Room;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace DinoTranslator.Desktop.Services;

/// <summary>
/// Conexão com a sala no servidor: presença do outro usuário e sinalização da chamada.
/// O token vai no cabeçalho Authorization, renovado pelo <see cref="AuthClient"/>.
/// </summary>
public sealed class RoomConnection : IAsyncDisposable
{
    private readonly HubConnection _hub;

    public RoomConnection(DesktopSettings settings, AuthClient auth)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(settings.ServerUri, RoomRoutes.Hub), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => auth.GetAccessTokenAsync();
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<Participant>(nameof(IRoomClient.PresenceChanged), participant => PresenceChanged?.Invoke(participant));
        _hub.On<ReceivedSignal>(nameof(IRoomClient.SignalReceived), signal => SignalReceived?.Invoke(signal));
        _hub.On(nameof(IRoomClient.SessionReplaced), () => SessionReplaced?.Invoke());
        _hub.Reconnecting += _ => NotifyStateChanged();
        _hub.Reconnected += _ => NotifyStateChanged();
        _hub.Closed += _ => NotifyStateChanged();
    }

    public event Action<Participant>? PresenceChanged;

    public event Action<ReceivedSignal>? SignalReceived;

    public event Action? SessionReplaced;

    public event Action? StateChanged;

    public HubConnectionState State => _hub.State;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync(cancellationToken);
            StateChanged?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _hub.StopAsync();
        StateChanged?.Invoke();
    }

    public Task<List<Participant>> GetParticipantsAsync(CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<List<Participant>>(RoomHubMethods.GetParticipants, cancellationToken);

    public Task SendSignalAsync(SignalMessage message, CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync(RoomHubMethods.SendSignal, message, cancellationToken);

    public ValueTask DisposeAsync() => _hub.DisposeAsync();

    private Task NotifyStateChanged()
    {
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }
}
