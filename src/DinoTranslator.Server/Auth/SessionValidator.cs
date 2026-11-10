using DinoTranslator.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace DinoTranslator.Server.Auth;

/// <summary>
/// Confere, a cada requisição autenticada, se a sessão do token ainda vale: a conta existe, não
/// está suspensa e o carimbo é o mesmo (troca de senha e suspensão mudam o carimbo). O resultado
/// fica em cache por poucos segundos (na memória e, com Redis, compartilhado entre as instâncias)
/// para o banco não receber uma consulta por requisição.
/// </summary>
public sealed class SessionValidator(HybridCache cache, IServiceScopeFactory scopes)
{
    /// <summary>Quanto tempo uma suspensão ou troca de senha pode levar para valer em todas as instâncias.</summary>
    public static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(20);

    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = CacheTime, LocalCacheExpiration = CacheTime };

    private sealed record Session(Guid Stamp, bool Disabled);

    private static string Key(Guid userId) => $"sessao:{userId:N}";

    public async Task<bool> IsValidAsync(Guid userId, Guid stamp, CancellationToken cancellationToken)
    {
        var session = await cache.GetOrCreateAsync(Key(userId), async token =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var found = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.SecurityStamp, u.DisabledAtUtc })
                .SingleOrDefaultAsync(token);
            return found is null ? null : new Session(found.SecurityStamp, found.DisabledAtUtc is not null);
        }, Entry, cancellationToken: cancellationToken);
        return session is { Disabled: false } && session.Stamp == stamp;
    }

    public ValueTask InvalidateAsync(Guid userId, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(Key(userId), cancellationToken);
}
