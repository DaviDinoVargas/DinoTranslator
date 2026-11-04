using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Data;
using DinoTranslator.Shared.Room;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Room;

/// <summary>
/// Sala única dos dois usuários: presença e sinalização WebRTC. O servidor só repassa
/// offer/answer/ICE; a mídia vai direto de um computador ao outro.
/// </summary>
[Authorize]
public sealed class RoomHub(AppDbContext db, PresenceTracker presence, ILogger<RoomHub> logger) : Hub<IRoomClient>
{
    private Guid CurrentUserId => Context.User?.GetUserId() ?? throw new HubException("Não autenticado.");

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId;
        var previous = presence.Register(userId, Context);
        if (previous is not null)
        {
            await Clients.Client(previous.ConnectionId).SessionReplaced();
            previous.Abort();
            logger.LogInformation("Conexão anterior da conta {UserId} substituída.", userId);
        }

        await NotifyPresenceAsync(userId, online: true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = CurrentUserId;
        if (presence.Unregister(userId, Context.ConnectionId))
        {
            await NotifyPresenceAsync(userId, online: false);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<IReadOnlyList<Participant>> GetParticipants()
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.CreatedAtUtc).ToListAsync(Context.ConnectionAborted);
        return users.Select(u => new Participant(AuthService.ToProfile(u), presence.IsOnline(u.Id))).ToList();
    }

    public async Task SendSignal(SignalMessage message)
    {
        if (message is null || !SignalTypes.All.Contains(message.Type ?? string.Empty))
        {
            throw new HubException("Tipo de sinal inválido.");
        }
        if (message.Payload is { Length: > SignalTypes.MaxPayloadLength })
        {
            throw new HubException("Sinal grande demais.");
        }

        var userId = CurrentUserId;
        var otherUserId = await OtherUserIdAsync(userId);
        if (otherUserId is not { } target || !presence.IsOnline(target))
        {
            throw new HubException("O outro usuário está offline.");
        }

        await Clients.User(target.ToString()).SignalReceived(new ReceivedSignal(userId, message.Type!, message.Payload));
    }

    private async Task NotifyPresenceAsync(Guid userId, bool online)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId);
        var otherUserId = await OtherUserIdAsync(userId);
        if (user is not null && otherUserId is { } target)
        {
            await Clients.User(target.ToString()).PresenceChanged(new Participant(AuthService.ToProfile(user), online));
        }
    }

    private Task<Guid?> OtherUserIdAsync(Guid userId) =>
        db.Users.AsNoTracking().Where(u => u.Id != userId).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
}
