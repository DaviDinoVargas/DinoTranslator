using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Data;
using DinoTranslator.Server.Social;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Realtime;

/// <summary>
/// Conexão em tempo real do app na versão paga: entra nos grupos da pessoa para receber as
/// mensagens e avisos, marca presença e repassa "digitando". As mensagens em si são criadas pela
/// API (POST), que manda o evento por aqui.
/// </summary>
[Authorize(Policy = AuthClaims.PlusPolicy)]
public sealed class AppHub(AppDbContext db, IPresenceStore presence, Notifier notifier, SocialAccess access) : Hub<IAppClient>
{
    private Guid CurrentUserId => Context.User?.GetUserId() ?? throw new HubException("Não autenticado.");

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId;
        var groups = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(Context.ConnectionAborted);
        foreach (var groupId in groups)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Notifier.GroupName(groupId), Context.ConnectionAborted);
        }
        if (await presence.ConnectedAsync(userId, Context.ConnectionId))
        {
            await notifier.PresenceAsync(userId, online: true);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = CurrentUserId;
        if (await presence.DisconnectedAsync(userId, Context.ConnectionId))
        {
            await notifier.PresenceAsync(userId, online: false);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>"Fulano está digitando" para quem pode ver o canal.</summary>
    public async Task Typing(Guid channelId)
    {
        var userId = CurrentUserId;
        if (await access.ReadableAsync(channelId, userId, Context.ConnectionAborted) is not { } readable)
        {
            throw new HubException("Canal não encontrado.");
        }
        var channel = readable.Channel;
        if (channel.IsDirect)
        {
            await Clients.User(channel.OtherDirectUser(userId)!.Value.ToString()).Typing(channelId, userId);
        }
        else
        {
            await Clients.OthersInGroup(Notifier.GroupName(channel.GroupId!.Value)).Typing(channelId, userId);
        }
    }
}
