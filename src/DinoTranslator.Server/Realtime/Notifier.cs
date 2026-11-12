using DinoTranslator.Server.Data;
using DinoTranslator.Server.Social;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Realtime;

/// <summary>
/// Manda os eventos da versão paga a quem pode recebê-los: grupos inteiros pelo grupo do SignalR,
/// conversas diretas só às duas contas. Também põe e tira as conexões abertas de uma pessoa dos
/// grupos quando ela entra, sai ou é removida (em qualquer instância, com o Redis).
/// </summary>
public sealed class Notifier(IHubContext<AppHub, IAppClient> hub, IPresenceStore presence, IServiceScopeFactory scopes, ILogger<Notifier> logger)
{
    public static string GroupName(Guid groupId) => $"g:{groupId:N}";

    public Task MessageCreatedAsync(ChannelRef channel, MessageInfo message) =>
        To(channel).MessageCreated(message);

    public Task MessageEditedAsync(ChannelRef channel, MessageInfo message) =>
        To(channel).MessageEdited(message);

    public Task MessageDeletedAsync(ChannelRef channel, long messageId) =>
        To(channel).MessageDeleted(channel.Id, messageId);

    public Task ReactionsChangedAsync(ChannelRef channel, long messageId, IReadOnlyList<ReactionInfo> reactions) =>
        To(channel).ReactionsChanged(channel.Id, messageId, reactions);

    public Task GroupChangedAsync(Guid groupId) => hub.Clients.Group(GroupName(groupId)).GroupChanged(groupId);

    public Task FriendsChangedAsync(params Guid[] userIds) => hub.Clients.Users(userIds.Select(u => u.ToString())).FriendsChanged();

    public Task DirectOpenedAsync(Guid userId, DirectInfo direct) => hub.Clients.User(userId.ToString()).DirectOpened(direct);

    public Task VoiceChangedAsync(Guid groupId, Guid channelId, IReadOnlyList<Guid> userIds) =>
        hub.Clients.Group(GroupName(groupId)).VoiceChanged(channelId, userIds);

    public Task DirectVoiceChangedAsync(ChannelRef channel, IReadOnlyList<Guid> userIds) =>
        To(channel).VoiceChanged(channel.Id, userIds);

    /// <summary>A pessoa entrou num grupo: as conexões abertas dela passam a receber os eventos dele.</summary>
    public async Task JoinedGroupAsync(Guid userId, Guid groupId)
    {
        foreach (var connection in await presence.ConnectionsAsync(userId))
        {
            await hub.Groups.AddToGroupAsync(connection, GroupName(groupId));
        }
        await GroupChangedAsync(groupId);
    }

    /// <summary>Saiu ou foi removida: as conexões dela param de receber na hora, e o app é avisado.</summary>
    public async Task LeftGroupAsync(Guid userId, Guid groupId)
    {
        foreach (var connection in await presence.ConnectionsAsync(userId))
        {
            await hub.Groups.RemoveFromGroupAsync(connection, GroupName(groupId));
        }
        await hub.Clients.User(userId.ToString()).GroupRemoved(groupId);
        await GroupChangedAsync(groupId);
    }

    /// <summary>Grupo apagado: avisa todos os membros e tira as conexões deles do grupo.</summary>
    public async Task GroupDeletedAsync(Guid groupId, IReadOnlyList<Guid> members)
    {
        await hub.Clients.Group(GroupName(groupId)).GroupRemoved(groupId);
        foreach (var userId in members)
        {
            foreach (var connection in await presence.ConnectionsAsync(userId))
            {
                await hub.Groups.RemoveFromGroupAsync(connection, GroupName(groupId));
            }
        }
    }

    /// <summary>Online/offline para os grupos e os amigos da pessoa.</summary>
    public async Task PresenceAsync(Guid userId, bool online)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var groups = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync();
            var friends = await db.Friendships.AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.LowId == userId || f.HighId == userId))
                .Select(f => f.LowId == userId ? f.HighId : f.LowId)
                .ToListAsync();
            if (groups.Count > 0)
            {
                await hub.Clients.Groups(groups.Select(GroupName)).PresenceChanged(userId, online);
            }
            if (friends.Count > 0)
            {
                await hub.Clients.Users(friends.Select(f => f.ToString())).PresenceChanged(userId, online);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Aviso de presença não enviado.");
        }
    }

    private IAppClient To(ChannelRef channel) => channel.IsDirect
        ? hub.Clients.Users([channel.DirectLowId!.Value.ToString(), channel.DirectHighId!.Value.ToString()])
        : hub.Clients.Group(GroupName(channel.GroupId!.Value));
}
