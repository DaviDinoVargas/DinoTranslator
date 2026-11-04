using DinoTranslator.Shared.Auth;

namespace DinoTranslator.Shared.Room;

public static class RoomRoutes
{
    public const string Hub = "/hubs/sala";
    public const string IceServers = "/api/ice-servers";
}

/// <summary>Mensagens de sinalização repassadas de um usuário ao outro.</summary>
public static class SignalTypes
{
    public const string Invite = "invite";
    public const string Accept = "accept";
    public const string Decline = "decline";
    public const string Hangup = "hangup";
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string Candidate = "candidate";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>([Invite, Accept, Decline, Hangup, Offer, Answer, Candidate], StringComparer.Ordinal);

    /// <summary>SDP e candidatos ICE cabem com folga; acima disso é abuso.</summary>
    public const int MaxPayloadLength = 32_000;
}

public sealed record SignalMessage(string Type, string? Payload);

public sealed record ReceivedSignal(Guid FromUserId, string Type, string? Payload);

public sealed record Participant(UserProfile Profile, bool Online);

public sealed record IceServer(IReadOnlyList<string> Urls, string? Username, string? Credential);

public sealed record IceServersResponse(IReadOnlyList<IceServer> IceServers, DateTimeOffset ExpiresAt);

/// <summary>Métodos que o servidor chama no cliente.</summary>
public interface IRoomClient
{
    Task PresenceChanged(Participant participant);

    Task SignalReceived(ReceivedSignal signal);

    /// <summary>A mesma conta abriu outra conexão; esta será encerrada.</summary>
    Task SessionReplaced();
}

/// <summary>Métodos que o cliente chama no servidor.</summary>
public static class RoomHubMethods
{
    public const string GetParticipants = "GetParticipants";
    public const string SendSignal = "SendSignal";
}
