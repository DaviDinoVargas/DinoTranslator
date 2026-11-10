using System.Text.Json.Serialization;

namespace DinoTranslator.Shared.Social;

/// <summary>Rotas da versão paga (todas exigem conta com Plus).</summary>
public static class SocialRoutes
{
    public const string Hub = "/hubs/app";
    public const string MyGroups = "/api/eu/grupos";
    public const string Groups = "/api/grupos";
    public static string Group(Guid id) => $"/api/grupos/{id}";
    public static string GroupChannels(Guid id) => $"/api/grupos/{id}/canais";
    public static string GroupInvites(Guid id) => $"/api/grupos/{id}/convites";
    public static string GroupMember(Guid groupId, Guid userId) => $"/api/grupos/{groupId}/membros/{userId}";
    public static string GroupMemberRole(Guid groupId, Guid userId) => $"/api/grupos/{groupId}/membros/{userId}/papel";
    public static string Channel(Guid id) => $"/api/canais/{id}";
    public static string ChannelMessages(Guid id) => $"/api/canais/{id}/mensagens";
    public static string Message(long id) => $"/api/mensagens/{id}";
    public static string Invite(string code) => $"/api/convites/{Uri.EscapeDataString(code)}";
    public static string InviteJoin(string code) => $"/api/convites/{Uri.EscapeDataString(code)}/entrar";
    public const string Friends = "/api/amigos";
    public static string Friend(Guid userId) => $"/api/amigos/{userId}";
    public static string Block(Guid userId) => $"/api/bloqueios/{userId}";
    public const string Directs = "/api/conversas";
    public static string Direct(Guid userId) => $"/api/conversas/{userId}";
    public static string ChannelVoice(Guid id) => $"/api/canais/{id}/voz";
    public const string LiveKitWebhook = "/api/livekit/webhook";
}

/// <summary>Limites que o servidor aplica (o app usa os mesmos para avisar antes).</summary>
public static class SocialLimits
{
    public const int MaxNameLength = 64;
    public const int MaxMessageLength = 4000;
    public const int MaxGroupsOwned = 20;
    public const int MaxGroupsJoined = 200;
    public const int MaxMembersPerGroup = 500;
    public const int MaxChannelsPerGroup = 50;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 50;
    public const int MaxInviteUses = 1000;
    public const int MaxInviteHours = 24 * 30;
    public const int MaxFriends = 1000;
}

[JsonConverter(typeof(JsonStringEnumConverter<GroupRole>))]
public enum GroupRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChannelKind>))]
public enum ChannelKind
{
    Text = 0,
    Voice = 1,
    Direct = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<FriendState>))]
public enum FriendState
{
    Friends = 0,
    Incoming = 1,
    Outgoing = 2,
}

public sealed record UserSummary(Guid Id, string UserName, string DisplayName, string Language);

public sealed record GroupSummary(Guid Id, string Name, GroupRole MyRole, int Members);

public sealed record ChannelInfo(Guid Id, Guid? GroupId, string Name, ChannelKind Kind, int Position, long LastMessageId);

public sealed record MemberInfo(UserSummary User, GroupRole Role, bool Online);

public sealed record GroupDetails(GroupSummary Group, IReadOnlyList<ChannelInfo> Channels, IReadOnlyList<MemberInfo> Members);

/// <summary>Mensagem como foi escrita: cada app traduz para o idioma de quem lê.</summary>
public sealed record MessageInfo(
    long Id,
    Guid ChannelId,
    UserSummary Author,
    string Content,
    string Language,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool Deleted,
    long? ReplyToId);

public sealed record MessagePage(IReadOnlyList<MessageInfo> Messages, bool HasMore);

public sealed record InviteInfo(string Code, Guid GroupId, string GroupName, int Members, DateTimeOffset ExpiresAt, int MaxUses, int Uses);

public sealed record FriendInfo(UserSummary User, FriendState State, bool Online);

public sealed record DirectInfo(ChannelInfo Channel, UserSummary Other, bool Online);

/// <summary>Entrada num canal de voz (ou chamada da conversa direta): o servidor de mídia e a permissão assinada.</summary>
public sealed record VoiceJoin(string Url, string Token, string Room, DateTimeOffset ExpiresAt);

public sealed record NameRequest(string Name);

public sealed record CreateChannelRequest(string Name, ChannelKind Kind);

public sealed record CreateInviteRequest(int? MaxUses = null, int? Hours = null);

public sealed record SendMessageRequest(string Content, long? ReplyToId = null);

public sealed record EditMessageRequest(string Content);

public sealed record FriendRequest(string UserName);

public sealed record RoleRequest(GroupRole Role);

/// <summary>Eventos que o servidor manda aos apps conectados (hub /hubs/app).</summary>
public interface IAppClient
{
    Task MessageCreated(MessageInfo message);

    Task MessageEdited(MessageInfo message);

    Task MessageDeleted(Guid channelId, long messageId);

    Task Typing(Guid channelId, Guid userId);

    Task PresenceChanged(Guid userId, bool online);

    /// <summary>Canais, membros, papéis ou nome do grupo mudaram: o app pede os detalhes de novo.</summary>
    Task GroupChanged(Guid groupId);

    /// <summary>Saiu, foi removido ou o grupo foi apagado.</summary>
    Task GroupRemoved(Guid groupId);

    Task FriendsChanged();

    Task DirectOpened(DirectInfo direct);

    /// <summary>Quem está no canal de voz agora.</summary>
    Task VoiceChanged(Guid channelId, IReadOnlyList<Guid> userIds);
}

/// <summary>Métodos que o app chama no hub.</summary>
public static class AppHubMethods
{
    public const string Typing = "Typing";
}
