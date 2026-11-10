using DinoTranslator.Server.Data;
using DinoTranslator.Shared.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace DinoTranslator.Server.Social;

/// <summary>Um canal como as regras de acesso o enxergam (muda pouco e vai para o cache).</summary>
public sealed record ChannelRef(Guid Id, Guid? GroupId, ChannelKind Kind, Guid? DirectLowId, Guid? DirectHighId)
{
    public bool IsDirect => Kind == ChannelKind.Direct;

    public Guid? OtherDirectUser(Guid userId) => DirectLowId == userId ? DirectHighId : DirectHighId == userId ? DirectLowId : null;
}

/// <summary>
/// Quem pode ver e mexer em quê. Toda rota e todo evento passam por aqui; as respostas ficam em
/// cache por pouco tempo e são apagadas na hora quando o papel de alguém muda, para o caminho
/// mais quente (mandar mensagem) não ir ao banco a cada vez.
/// </summary>
public sealed class SocialAccess(AppDbContext db, HybridCache cache)
{
    private static readonly HybridCacheEntryOptions Short = new() { Expiration = TimeSpan.FromSeconds(60), LocalCacheExpiration = TimeSpan.FromSeconds(30) };

    private static string RoleKey(Guid groupId, Guid userId) => $"papel:{groupId:N}:{userId:N}";

    private static string ChannelKey(Guid channelId) => $"canal:{channelId:N}";

    /// <summary>O papel da pessoa no grupo, ou null se não for membro.</summary>
    public async Task<GroupRole?> RoleAsync(Guid groupId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await cache.GetOrCreateAsync(RoleKey(groupId, userId), async token =>
            await db.GroupMembers.AsNoTracking()
                .Where(m => m.GroupId == groupId && m.UserId == userId)
                .Select(m => (int)m.Role)
                .Cast<int?>()
                .SingleOrDefaultAsync(token) ?? -1,
            Short, cancellationToken: cancellationToken);
        return role < 0 ? null : (GroupRole)role;
    }

    public ValueTask ForgetRoleAsync(Guid groupId, Guid userId) => cache.RemoveAsync(RoleKey(groupId, userId));

    public async Task<ChannelRef?> ChannelAsync(Guid channelId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(ChannelKey(channelId), async token =>
            await db.Channels.AsNoTracking()
                .Where(c => c.Id == channelId)
                .Select(c => new ChannelRef(c.Id, c.GroupId, c.Kind, c.DirectLowId, c.DirectHighId))
                .SingleOrDefaultAsync(token),
            Short, cancellationToken: cancellationToken);

    public ValueTask ForgetChannelAsync(Guid channelId) => cache.RemoveAsync(ChannelKey(channelId));

    /// <summary>O canal, se a pessoa pode lê-lo e escrever nele (membro do grupo ou parte da conversa).</summary>
    public async Task<(ChannelRef Channel, GroupRole? Role)?> ReadableAsync(Guid channelId, Guid userId, CancellationToken cancellationToken)
    {
        if (await ChannelAsync(channelId, cancellationToken) is not { } channel)
        {
            return null;
        }
        if (channel.IsDirect)
        {
            return channel.OtherDirectUser(userId) is null ? null : (channel, null);
        }
        return await RoleAsync(channel.GroupId!.Value, userId, cancellationToken) is { } role ? (channel, role) : null;
    }

    /// <summary>Uma das duas bloqueou a outra.</summary>
    public Task<bool> BlockedAsync(Guid a, Guid b, CancellationToken cancellationToken) =>
        db.Blocks.AsNoTracking().AnyAsync(x => (x.UserId == a && x.BlockedId == b) || (x.UserId == b && x.BlockedId == a), cancellationToken);
}
