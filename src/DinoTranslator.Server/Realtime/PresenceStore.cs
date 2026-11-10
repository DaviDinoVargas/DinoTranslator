using System.Collections.Concurrent;
using StackExchange.Redis;

namespace DinoTranslator.Server.Realtime;

/// <summary>
/// Quem está conectado e por quais conexões (uma pessoa pode ter o app aberto em mais de um
/// computador). Com Redis vale para todas as instâncias; cada conexão tem validade renovada pela
/// instância dona dela, então uma instância que cai não deixa ninguém "online" para sempre.
/// </summary>
public interface IPresenceStore
{
    /// <summary>Registra a conexão; true se a pessoa acabou de ficar online.</summary>
    Task<bool> ConnectedAsync(Guid userId, string connectionId);

    /// <summary>Tira a conexão; true se a pessoa acabou de ficar offline.</summary>
    Task<bool> DisconnectedAsync(Guid userId, string connectionId);

    Task<IReadOnlyList<string>> ConnectionsAsync(Guid userId);

    Task<IReadOnlySet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds);
}

/// <summary>Uma instância só: tudo na memória.</summary>
public sealed class MemoryPresenceStore : IPresenceStore
{
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _connections = new();

    public Task<bool> ConnectedAsync(Guid userId, string connectionId)
    {
        var set = _connections.GetOrAdd(userId, _ => []);
        lock (set)
        {
            return Task.FromResult(set.Add(connectionId) && set.Count == 1);
        }
    }

    public Task<bool> DisconnectedAsync(Guid userId, string connectionId)
    {
        if (!_connections.TryGetValue(userId, out var set))
        {
            return Task.FromResult(false);
        }
        lock (set)
        {
            return Task.FromResult(set.Remove(connectionId) && set.Count == 0);
        }
    }

    public Task<IReadOnlyList<string>> ConnectionsAsync(Guid userId)
    {
        if (!_connections.TryGetValue(userId, out var set))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        lock (set)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. set]);
        }
    }

    public Task<IReadOnlySet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds)
    {
        var online = new HashSet<Guid>();
        foreach (var id in userIds)
        {
            if (_connections.TryGetValue(id, out var set))
            {
                lock (set)
                {
                    if (set.Count > 0)
                    {
                        online.Add(id);
                    }
                }
            }
        }
        return Task.FromResult<IReadOnlySet<Guid>>(online);
    }
}

/// <summary>
/// Várias instâncias: um conjunto ordenado por pessoa no Redis, com a validade de cada conexão
/// como pontuação. A instância dona renova as suas a cada 30 s (<see cref="PresenceHeartbeat"/>).
/// </summary>
public sealed class RedisPresenceStore(IConnectionMultiplexer redis, TimeProvider time) : IPresenceStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, Guid> _local = new();

    private static RedisKey Key(Guid userId) => $"dino:presenca:{userId:N}";

    private double Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    private double Expiry => time.GetUtcNow().Add(Lifetime).ToUnixTimeMilliseconds();

    public async Task<bool> ConnectedAsync(Guid userId, string connectionId)
    {
        _local[connectionId] = userId;
        var db = redis.GetDatabase();
        var key = Key(userId);
        await db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, Now);
        await db.SortedSetAddAsync(key, connectionId, Expiry);
        await db.KeyExpireAsync(key, Lifetime * 2);
        return await db.SortedSetLengthAsync(key, Now, double.PositiveInfinity) == 1;
    }

    public async Task<bool> DisconnectedAsync(Guid userId, string connectionId)
    {
        _local.TryRemove(connectionId, out _);
        var db = redis.GetDatabase();
        var key = Key(userId);
        var removed = await db.SortedSetRemoveAsync(key, connectionId);
        return removed && await db.SortedSetLengthAsync(key, Now, double.PositiveInfinity) == 0;
    }

    public async Task<IReadOnlyList<string>> ConnectionsAsync(Guid userId)
    {
        var values = await redis.GetDatabase().SortedSetRangeByScoreAsync(Key(userId), Now, double.PositiveInfinity);
        return [.. values.Select(v => v.ToString())];
    }

    public async Task<IReadOnlySet<Guid>> OnlineAsync(IReadOnlyCollection<Guid> userIds)
    {
        var db = redis.GetDatabase();
        var now = Now;
        var counts = userIds.Select(id => (id, count: db.SortedSetLengthAsync(Key(id), now, double.PositiveInfinity))).ToList();
        await Task.WhenAll(counts.Select(c => c.count));
        return counts.Where(c => c.count.Result > 0).Select(c => c.id).ToHashSet();
    }

    /// <summary>Renova a validade das conexões desta instância.</summary>
    public async Task RenewAsync()
    {
        var db = redis.GetDatabase();
        var expiry = Expiry;
        var batch = db.CreateBatch();
        var pending = new List<Task>();
        foreach (var (connectionId, userId) in _local)
        {
            pending.Add(batch.SortedSetAddAsync(Key(userId), connectionId, expiry));
            pending.Add(batch.KeyExpireAsync(Key(userId), Lifetime * 2));
        }
        batch.Execute();
        await Task.WhenAll(pending);
    }
}

public sealed class PresenceHeartbeat(RedisPresenceStore store, ILogger<PresenceHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await store.RenewAsync();
            }
            catch (RedisException ex)
            {
                logger.LogWarning(ex, "Não foi possível renovar a presença no Redis.");
            }
        }
    }
}
