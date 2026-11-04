using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace DinoTranslator.Server.Room;

/// <summary>
/// Uma conexão ativa por usuário. Uma conexão nova da mesma conta substitui a anterior,
/// para a sinalização da chamada nunca ir para duas janelas ao mesmo tempo.
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, HubCallerContext> _connections = new();

    /// <summary>Registra a conexão e devolve a anterior da mesma conta, se houver.</summary>
    public HubCallerContext? Register(Guid userId, HubCallerContext connection)
    {
        HubCallerContext? previous = null;
        _connections.AddOrUpdate(userId, connection, (_, existing) =>
        {
            previous = existing;
            return connection;
        });
        return previous;
    }

    /// <summary>Remove só se esta ainda for a conexão ativa da conta.</summary>
    public bool Unregister(Guid userId, string connectionId) =>
        _connections.TryGetValue(userId, out var current)
        && current.ConnectionId == connectionId
        && _connections.TryRemove(new KeyValuePair<Guid, HubCallerContext>(userId, current));

    public bool IsOnline(Guid userId) => _connections.ContainsKey(userId);
}
