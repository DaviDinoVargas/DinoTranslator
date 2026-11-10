using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Data;
using DinoTranslator.Server.Realtime;
using DinoTranslator.Shared.Social;
using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Social;

/// <summary>Amigos, bloqueios e conversas diretas.</summary>
public sealed class FriendService(AppDbContext db, SocialAccess access, IPresenceStore presence, Notifier notifier, TimeProvider time)
{
    public async Task<List<FriendInfo>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Friendships.AsNoTracking()
            .Where(f => f.LowId == userId || f.HighId == userId)
            .Select(f => new { Other = f.LowId == userId ? f.HighId : f.LowId, f.Status, f.RequestedById })
            .Join(db.Users, f => f.Other, u => u.Id, (f, u) => new { f.Status, f.RequestedById, User = u })
            .ToListAsync(cancellationToken);
        var online = await presence.OnlineAsync(rows.Where(r => r.Status == FriendshipStatus.Accepted).Select(r => r.User.Id).ToList());
        return [.. rows
            .Select(r => new FriendInfo(
                Text.Summary(r.User),
                r.Status == FriendshipStatus.Accepted ? FriendState.Friends : r.RequestedById == userId ? FriendState.Outgoing : FriendState.Incoming,
                online.Contains(r.User.Id)))
            .OrderBy(f => f.State).ThenBy(f => f.User.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Pede amizade pelo nome de usuário; se a outra pessoa já tinha pedido, vira amizade.</summary>
    public async Task<Outcome<FriendInfo>> RequestAsync(Guid userId, string? userName, CancellationToken cancellationToken)
    {
        var normalized = Credentials.NormalizeUserName(userName);
        var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserName == normalized && u.DisabledAtUtc == null, cancellationToken);
        // Bloqueio responde igual a "não existe": quem bloqueou não aparece.
        if (target is null || target.Id == userId || await access.BlockedAsync(userId, target.Id, cancellationToken))
        {
            return Problem.NotFound;
        }
        var (low, high) = Pairs.Of(userId, target.Id);
        var existing = await db.Friendships.SingleOrDefaultAsync(f => f.LowId == low && f.HighId == high, cancellationToken);
        if (existing is { Status: FriendshipStatus.Accepted })
        {
            return Problem.Conflict("Vocês já são amigos.");
        }
        if (existing is not null && existing.RequestedById == userId)
        {
            return Problem.Conflict("Pedido já enviado.");
        }
        if (existing is not null)
        {
            existing.Status = FriendshipStatus.Accepted;
        }
        else
        {
            if (await db.Friendships.CountAsync(f => f.LowId == userId || f.HighId == userId, cancellationToken) >= SocialLimits.MaxFriends)
            {
                return Problem.Invalid($"Você já tem {SocialLimits.MaxFriends} amigos e pedidos, o máximo.");
            }
            db.Friendships.Add(new Friendship { LowId = low, HighId = high, RequestedById = userId, Status = FriendshipStatus.Pending, CreatedAtUtc = time.GetUtcNow().UtcDateTime });
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Problem.Conflict("Pedido já enviado.");
        }
        await notifier.FriendsChangedAsync(userId, target.Id);
        var accepted = existing is not null;
        var online = accepted && (await presence.OnlineAsync([target.Id])).Contains(target.Id);
        return Outcome<FriendInfo>.Ok(new FriendInfo(Text.Summary(target), accepted ? FriendState.Friends : FriendState.Outgoing, online));
    }

    /// <summary>Desfaz a amizade, recusa ou cancela o pedido.</summary>
    public async Task<Outcome<Done>> RemoveAsync(Guid userId, Guid otherId, CancellationToken cancellationToken)
    {
        var (low, high) = Pairs.Of(userId, otherId);
        var removed = await db.Friendships.Where(f => f.LowId == low && f.HighId == high).ExecuteDeleteAsync(cancellationToken);
        if (removed == 0)
        {
            return Problem.NotFound;
        }
        await notifier.FriendsChangedAsync(userId, otherId);
        return Outcome<Done>.Ok(Done.Value);
    }

    public async Task<Outcome<Done>> BlockAsync(Guid userId, Guid otherId, CancellationToken cancellationToken)
    {
        if (otherId == userId || !await db.Users.AnyAsync(u => u.Id == otherId, cancellationToken))
        {
            return Problem.NotFound;
        }
        if (!await db.Blocks.AnyAsync(b => b.UserId == userId && b.BlockedId == otherId, cancellationToken))
        {
            db.Blocks.Add(new Block { UserId = userId, BlockedId = otherId, CreatedAtUtc = time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(cancellationToken);
        }
        var (low, high) = Pairs.Of(userId, otherId);
        await db.Friendships.Where(f => f.LowId == low && f.HighId == high).ExecuteDeleteAsync(cancellationToken);
        await notifier.FriendsChangedAsync(userId, otherId);
        return Outcome<Done>.Ok(Done.Value);
    }

    public async Task<Outcome<Done>> UnblockAsync(Guid userId, Guid otherId, CancellationToken cancellationToken)
    {
        await db.Blocks.Where(b => b.UserId == userId && b.BlockedId == otherId).ExecuteDeleteAsync(cancellationToken);
        return Outcome<Done>.Ok(Done.Value);
    }

    public async Task<List<DirectInfo>> DirectsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Channels.AsNoTracking()
            .Where(c => c.Kind == ChannelKind.Direct && (c.DirectLowId == userId || c.DirectHighId == userId))
            .OrderByDescending(c => c.LastMessageId)
            .Select(c => new { Channel = c, Other = c.DirectLowId == userId ? c.DirectHighId : c.DirectLowId })
            .Join(db.Users, c => c.Other, u => u.Id, (c, u) => new { c.Channel, User = u })
            .Take(200)
            .ToListAsync(cancellationToken);
        var online = await presence.OnlineAsync(rows.Select(r => r.User.Id).ToList());
        return [.. rows.Select(r => new DirectInfo(Info(r.Channel), Text.Summary(r.User), online.Contains(r.User.Id)))];
    }

    /// <summary>Abre (ou acha) a conversa direta: só entre amigos ou quem está num mesmo grupo, sem bloqueio.</summary>
    public async Task<Outcome<DirectInfo>> OpenDirectAsync(Guid userId, Guid otherId, CancellationToken cancellationToken)
    {
        var other = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == otherId && u.DisabledAtUtc == null, cancellationToken);
        if (other is null || otherId == userId || await access.BlockedAsync(userId, otherId, cancellationToken))
        {
            return Problem.NotFound;
        }
        var (low, high) = Pairs.Of(userId, otherId);
        var friends = await db.Friendships.AnyAsync(f => f.LowId == low && f.HighId == high && f.Status == FriendshipStatus.Accepted, cancellationToken);
        var shareGroup = friends || await db.GroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId)
            .Intersect(db.GroupMembers.Where(m => m.UserId == otherId).Select(m => m.GroupId))
            .AnyAsync(cancellationToken);
        if (!friends && !shareGroup)
        {
            return Problem.Forbidden("Para conversar, sejam amigos ou estejam num mesmo grupo.");
        }

        var channel = await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.DirectLowId == low && c.DirectHighId == high, cancellationToken);
        if (channel is null)
        {
            channel = new Channel { Id = Guid.NewGuid(), Name = "direta", Kind = ChannelKind.Direct, DirectLowId = low, DirectHighId = high, CreatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.Channels.Add(channel);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A outra pessoa abriu ao mesmo tempo: o par é único, então já existe.
                db.ChangeTracker.Clear();
                channel = await db.Channels.AsNoTracking().SingleAsync(c => c.DirectLowId == low && c.DirectHighId == high, cancellationToken);
            }
            var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, cancellationToken);
            await notifier.DirectOpenedAsync(otherId, new DirectInfo(Info(channel), Text.Summary(me), (await presence.OnlineAsync([userId])).Contains(userId)));
        }
        var online = (await presence.OnlineAsync([otherId])).Contains(otherId);
        return Outcome<DirectInfo>.Ok(new DirectInfo(Info(channel), Text.Summary(other), online));
    }

    private static ChannelInfo Info(Channel c) => new(c.Id, c.GroupId, c.Name, c.Kind, c.Position, c.LastMessageId);
}
