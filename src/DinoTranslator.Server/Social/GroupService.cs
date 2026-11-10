using System.Security.Cryptography;
using DinoTranslator.Server.Data;
using DinoTranslator.Server.Realtime;
using DinoTranslator.Shared.Social;
using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Social;

/// <summary>Grupos, canais, convites e membros.</summary>
public sealed class GroupService(AppDbContext db, SocialAccess access, IPresenceStore presence, Notifier notifier, TimeProvider time)
{
    private const string InviteAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    private const int InviteLength = 10;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public Task<List<GroupSummary>> MineAsync(Guid userId, CancellationToken cancellationToken) =>
        db.GroupMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.Groups, m => m.GroupId, g => g.Id, (m, g) => new { g.Id, g.Name, m.Role, g.CreatedAtUtc })
            .OrderBy(g => g.CreatedAtUtc)
            .Select(g => new GroupSummary(g.Id, g.Name, g.Role, db.GroupMembers.Count(x => x.GroupId == g.Id)))
            .ToListAsync(cancellationToken);

    public async Task<Outcome<GroupDetails>> CreateAsync(Guid userId, string? name, CancellationToken cancellationToken)
    {
        if (Text.CleanName(name) is not { } clean)
        {
            return Problem.Invalid($"O nome precisa ter de 1 a {SocialLimits.MaxNameLength} caracteres.");
        }
        if (await db.Groups.CountAsync(g => g.OwnerId == userId, cancellationToken) >= SocialLimits.MaxGroupsOwned)
        {
            return Problem.Invalid($"Você já criou {SocialLimits.MaxGroupsOwned} grupos, o máximo.");
        }
        if (await db.GroupMembers.CountAsync(m => m.UserId == userId, cancellationToken) >= SocialLimits.MaxGroupsJoined)
        {
            return Problem.Invalid($"Você já está em {SocialLimits.MaxGroupsJoined} grupos, o máximo.");
        }

        var now = Now;
        var group = new Group { Id = Guid.NewGuid(), Name = clean, OwnerId = userId, CreatedAtUtc = now };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId, Role = GroupRole.Owner, JoinedAtUtc = now });
        db.Channels.Add(new Channel { Id = Guid.NewGuid(), GroupId = group.Id, Name = "geral", Kind = ChannelKind.Text, Position = 0, CreatedAtUtc = now });
        db.Channels.Add(new Channel { Id = Guid.NewGuid(), GroupId = group.Id, Name = "Conversa", Kind = ChannelKind.Voice, Position = 1, CreatedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);
        await access.ForgetRoleAsync(group.Id, userId);
        await notifier.JoinedGroupAsync(userId, group.Id);
        return await DetailsAsync(userId, group.Id, cancellationToken);
    }

    public async Task<Outcome<GroupDetails>> DetailsAsync(Guid userId, Guid groupId, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, userId, cancellationToken) is not { } myRole)
        {
            return Problem.NotFound;
        }
        var group = await db.Groups.AsNoTracking().SingleAsync(g => g.Id == groupId, cancellationToken);
        var channels = await db.Channels.AsNoTracking()
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.Kind).ThenBy(c => c.Position)
            .Select(c => new ChannelInfo(c.Id, c.GroupId, c.Name, c.Kind, c.Position, c.LastMessageId))
            .ToListAsync(cancellationToken);
        var members = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId)
            .OrderByDescending(m => m.Role).ThenBy(m => m.JoinedAtUtc)
            .Select(m => new { m.User, m.Role })
            .ToListAsync(cancellationToken);
        var online = await presence.OnlineAsync(members.Select(m => m.User.Id).ToList());
        return Outcome<GroupDetails>.Ok(new GroupDetails(
            new GroupSummary(group.Id, group.Name, myRole, members.Count),
            channels,
            [.. members.Select(m => new MemberInfo(Text.Summary(m.User), m.Role, online.Contains(m.User.Id)))]));
    }

    public async Task<Outcome<Done>> RenameAsync(Guid userId, Guid groupId, string? name, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, userId, cancellationToken) is not { } role)
        {
            return Problem.NotFound;
        }
        if (role < GroupRole.Admin)
        {
            return Problem.Forbidden();
        }
        if (Text.CleanName(name) is not { } clean)
        {
            return Problem.Invalid($"O nome precisa ter de 1 a {SocialLimits.MaxNameLength} caracteres.");
        }
        await db.Groups.Where(g => g.Id == groupId).ExecuteUpdateAsync(set => set.SetProperty(g => g.Name, clean), cancellationToken);
        await notifier.GroupChangedAsync(groupId);
        return Outcome<Done>.Ok(Done.Value);
    }

    public async Task<Outcome<Done>> DeleteAsync(Guid userId, Guid groupId, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, userId, cancellationToken) is not { } role)
        {
            return Problem.NotFound;
        }
        if (role != GroupRole.Owner)
        {
            return Problem.Forbidden("Só o dono apaga o grupo.");
        }
        var members = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == groupId).Select(m => m.UserId).ToListAsync(cancellationToken);
        var channels = await db.Channels.AsNoTracking().Where(c => c.GroupId == groupId).Select(c => c.Id).ToListAsync(cancellationToken);
        await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync(cancellationToken);
        foreach (var member in members)
        {
            await access.ForgetRoleAsync(groupId, member);
        }
        foreach (var channel in channels)
        {
            await access.ForgetChannelAsync(channel);
        }
        await notifier.GroupDeletedAsync(groupId, members);
        return Outcome<Done>.Ok(Done.Value);
    }

    public async Task<Outcome<ChannelInfo>> CreateChannelAsync(Guid userId, Guid groupId, CreateChannelRequest? request, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, userId, cancellationToken) is not { } role)
        {
            return Problem.NotFound;
        }
        if (role < GroupRole.Admin)
        {
            return Problem.Forbidden();
        }
        if (request is null || request.Kind is not (ChannelKind.Text or ChannelKind.Voice))
        {
            return Problem.Invalid("Tipo de canal inválido.");
        }
        if (Text.CleanName(request.Name) is not { } clean)
        {
            return Problem.Invalid($"O nome precisa ter de 1 a {SocialLimits.MaxNameLength} caracteres.");
        }
        var count = await db.Channels.CountAsync(c => c.GroupId == groupId, cancellationToken);
        if (count >= SocialLimits.MaxChannelsPerGroup)
        {
            return Problem.Invalid($"O grupo já tem {SocialLimits.MaxChannelsPerGroup} canais, o máximo.");
        }
        var position = await db.Channels.Where(c => c.GroupId == groupId && c.Kind == request.Kind).Select(c => (int?)c.Position).MaxAsync(cancellationToken) ?? -1;
        var channel = new Channel { Id = Guid.NewGuid(), GroupId = groupId, Name = clean, Kind = request.Kind, Position = position + 1, CreatedAtUtc = Now };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(cancellationToken);
        await notifier.GroupChangedAsync(groupId);
        return Outcome<ChannelInfo>.Ok(new ChannelInfo(channel.Id, groupId, channel.Name, channel.Kind, channel.Position, 0));
    }

    public async Task<Outcome<Done>> DeleteChannelAsync(Guid userId, Guid channelId, CancellationToken cancellationToken)
    {
        if (await access.ReadableAsync(channelId, userId, cancellationToken) is not { } readable || readable.Channel.IsDirect)
        {
            return Problem.NotFound;
        }
        if (readable.Role < GroupRole.Admin)
        {
            return Problem.Forbidden();
        }
        await db.Channels.Where(c => c.Id == channelId).ExecuteDeleteAsync(cancellationToken);
        await access.ForgetChannelAsync(channelId);
        await notifier.GroupChangedAsync(readable.Channel.GroupId!.Value);
        return Outcome<Done>.Ok(Done.Value);
    }

    public async Task<Outcome<InviteInfo>> CreateInviteAsync(Guid userId, Guid groupId, CreateInviteRequest? request, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, userId, cancellationToken) is null)
        {
            return Problem.NotFound;
        }
        var maxUses = request?.MaxUses ?? 0;
        var hours = request?.Hours ?? 24 * 7;
        if (maxUses is < 0 or > SocialLimits.MaxInviteUses || hours is < 1 or > SocialLimits.MaxInviteHours)
        {
            return Problem.Invalid($"Convite inválido: até {SocialLimits.MaxInviteUses} usos e até {SocialLimits.MaxInviteHours / 24} dias.");
        }
        var invite = new GroupInvite
        {
            Code = RandomNumberGenerator.GetString(InviteAlphabet, InviteLength),
            GroupId = groupId,
            CreatedById = userId,
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now.AddHours(hours),
            MaxUses = maxUses,
        };
        db.GroupInvites.Add(invite);
        await db.SaveChangesAsync(cancellationToken);
        return (await PreviewAsync(invite.Code, cancellationToken)).Value is { } info ? Outcome<InviteInfo>.Ok(info) : Problem.NotFound;
    }

    public async Task<Outcome<InviteInfo>> PreviewAsync(string? code, CancellationToken cancellationToken)
    {
        var normalized = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length != InviteLength)
        {
            return Problem.NotFound;
        }
        var now = Now;
        var invite = await db.GroupInvites.AsNoTracking()
            .Where(i => i.Code == normalized && i.ExpiresAtUtc > now && (i.MaxUses == 0 || i.Uses < i.MaxUses))
            .Join(db.Groups, i => i.GroupId, g => g.Id, (i, g) => new { i, g.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (invite is null)
        {
            return Problem.NotFound;
        }
        var members = await db.GroupMembers.CountAsync(m => m.GroupId == invite.i.GroupId, cancellationToken);
        return Outcome<InviteInfo>.Ok(new InviteInfo(invite.i.Code, invite.i.GroupId, invite.Name, members,
            Text.Utc(invite.i.ExpiresAtUtc), invite.i.MaxUses, invite.i.Uses));
    }

    public async Task<Outcome<GroupDetails>> JoinAsync(Guid userId, string? code, CancellationToken cancellationToken)
    {
        if ((await PreviewAsync(code, cancellationToken)).Value is not { } invite)
        {
            return Problem.NotFound;
        }
        if (await access.RoleAsync(invite.GroupId, userId, cancellationToken) is not null)
        {
            return await DetailsAsync(userId, invite.GroupId, cancellationToken);
        }
        if (invite.Members >= SocialLimits.MaxMembersPerGroup)
        {
            return Problem.Invalid("O grupo está cheio.");
        }
        if (await db.GroupMembers.CountAsync(m => m.UserId == userId, cancellationToken) >= SocialLimits.MaxGroupsJoined)
        {
            return Problem.Invalid($"Você já está em {SocialLimits.MaxGroupsJoined} grupos, o máximo.");
        }

        var now = Now;
        // Uso contado no próprio banco: dois cliques ao mesmo tempo não passam do limite.
        var claimed = await db.GroupInvites
            .Where(i => i.Code == invite.Code && i.ExpiresAtUtc > now && (i.MaxUses == 0 || i.Uses < i.MaxUses))
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.Uses, i => i.Uses + 1), cancellationToken);
        if (claimed == 0)
        {
            return Problem.NotFound;
        }
        db.GroupMembers.Add(new GroupMember { GroupId = invite.GroupId, UserId = userId, Role = GroupRole.Member, JoinedAtUtc = now });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Entrou ao mesmo tempo por outro caminho: já é membro.
        }
        await access.ForgetRoleAsync(invite.GroupId, userId);
        await notifier.JoinedGroupAsync(userId, invite.GroupId);
        return await DetailsAsync(userId, invite.GroupId, cancellationToken);
    }

    /// <summary>Sair (a própria pessoa) ou remover alguém de papel menor (administradores e dono).</summary>
    public async Task<Outcome<Done>> RemoveMemberAsync(Guid actorId, Guid groupId, Guid targetId, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, actorId, cancellationToken) is not { } actorRole
            || await access.RoleAsync(groupId, targetId, cancellationToken) is not { } targetRole)
        {
            return Problem.NotFound;
        }
        if (actorId == targetId && actorRole == GroupRole.Owner)
        {
            return Problem.Invalid("O dono não sai do grupo: passe o grupo para outra pessoa ou apague-o.");
        }
        if (actorId != targetId && (actorRole < GroupRole.Admin || targetRole >= actorRole))
        {
            return Problem.Forbidden();
        }
        await db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId == targetId).ExecuteDeleteAsync(cancellationToken);
        await access.ForgetRoleAsync(groupId, targetId);
        await notifier.LeftGroupAsync(targetId, groupId);
        return Outcome<Done>.Ok(Done.Value);
    }

    /// <summary>Só o dono: torna administrador, volta a membro ou passa o grupo (Owner).</summary>
    public async Task<Outcome<Done>> SetRoleAsync(Guid actorId, Guid groupId, Guid targetId, GroupRole role, CancellationToken cancellationToken)
    {
        if (await access.RoleAsync(groupId, actorId, cancellationToken) is not { } actorRole
            || await access.RoleAsync(groupId, targetId, cancellationToken) is not { } targetRole)
        {
            return Problem.NotFound;
        }
        if (actorRole != GroupRole.Owner || actorId == targetId || !Enum.IsDefined(role))
        {
            return Problem.Forbidden("Só o dono muda os papéis.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (role == GroupRole.Owner)
        {
            await db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId == actorId).ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, GroupRole.Admin), cancellationToken);
            await db.Groups.Where(g => g.Id == groupId).ExecuteUpdateAsync(set => set.SetProperty(g => g.OwnerId, targetId), cancellationToken);
        }
        await db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId == targetId).ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, role), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await access.ForgetRoleAsync(groupId, actorId);
        await access.ForgetRoleAsync(groupId, targetId);
        await notifier.GroupChangedAsync(groupId);
        return Outcome<Done>.Ok(Done.Value);
    }
}
