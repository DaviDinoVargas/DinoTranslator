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
    public static string MessageReaction(long id, string emoji) => $"/api/mensagens/{id}/reacoes/{Uri.EscapeDataString(emoji)}";
    public static string MessagePin(long id) => $"/api/mensagens/{id}/fixar";
    public static string ChannelPins(Guid id) => $"/api/canais/{id}/fixadas";
    public const string Search = "/api/busca";
    public const string Glossary = "/api/glossario";
    public static string GroupGlossary(Guid id) => $"/api/grupos/{id}/glossario";
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
    public const int MaxPinsPerChannel = 50;
    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 100;
    public const int MaxSearchResults = 50;

    /// <summary>Reações rápidas aceitas (lista fixa: nada de texto livre como reação).</summary>
    public static IReadOnlyList<string> Reactions { get; } = ["👍", "❤️", "😂", "😮", "😢", "🙏", "🎉", "👀", "🔥", "✅"];
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
/// <param name="Reactions">Reações e quem reagiu.</param>
/// <param name="Mentions">Contas mencionadas com @usuário (só quem pode ver o canal).</param>
/// <param name="Reply">A mensagem respondida, resumida para a citação.</param>
public sealed record MessageInfo(
    long Id,
    Guid ChannelId,
    UserSummary Author,
    string Content,
    string Language,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool Deleted,
    long? ReplyToId,
    IReadOnlyList<ReactionInfo>? Reactions = null,
    IReadOnlyList<Guid>? Mentions = null,
    bool Pinned = false,
    MessageQuote? Reply = null);

public sealed record ReactionInfo(string Emoji, IReadOnlyList<Guid> Users);

/// <summary>Citação da mensagem respondida: autor, idioma e o começo do texto.</summary>
public sealed record MessageQuote(long Id, string AuthorName, string Content, string Language, bool Deleted);

/// <summary>Resultado da busca: a mensagem e onde ela está.</summary>
public sealed record SearchResult(MessageInfo Message, Guid? GroupId, string ChannelName);

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

    Task ReactionsChanged(Guid channelId, long messageId, IReadOnlyList<ReactionInfo> reactions);

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

    /// <summary>Sinal da chamada 1:1 pela conta (ver <see cref="CallSignal"/>).</summary>
    Task DirectCall(CallSignalInfo signal);

    /// <summary>O glossário mudou: o pessoal (null, de outro aparelho da conta) ou o do grupo.</summary>
    Task GlossaryChanged(Guid? groupId);
}

/// <summary>
/// Termo do glossário (nomes, marcas, jargão): o reconhecimento de fala recebe a lista como dica e
/// a tradução mantém o termo como está (sem <see cref="Translation"/>) ou usa a tradução dada para
/// o idioma <see cref="Language"/>.
/// </summary>
public sealed record GlossaryEntry(string Term, string? Translation = null, string? Language = null);

public sealed record GlossaryRequest(IReadOnlyList<GlossaryEntry>? Entries);

public static class GlossaryLimits
{
    public const int MaxTermLength = 60;
    public const int MaxPersonal = 200;
    public const int MaxGroup = 300;
}

/// <summary>
/// Chamada 1:1 pela conta: o app troca pelo servidor o mesmo convite e a mesma resposta da chamada
/// por código. A mídia continua direta entre os dois computadores e cifrada de ponta a ponta.
/// </summary>
public enum CallSignal
{
    /// <summary>Quem liga manda o convite (código da chamada).</summary>
    Ring = 0,

    /// <summary>Quem atende manda a resposta (código da chamada).</summary>
    Answer = 1,

    /// <summary>Quem recebeu recusou.</summary>
    Decline = 2,

    /// <summary>Quem ligou desistiu antes de atenderem (ou o tempo acabou).</summary>
    Cancel = 3,

    /// <summary>Quem recebeu já está em outra chamada.</summary>
    Busy = 4,

    /// <summary>Só do servidor: outro aparelho da mesma pessoa já atendeu ou recusou; este para de tocar.</summary>
    Handled = 5,
}

/// <summary>Sinal recebido: de qual conversa direta, de quem e de qual chamada.</summary>
public sealed record CallSignalInfo(Guid ChannelId, Guid FromUserId, string CallId, CallSignal Signal, string? Code);

public static class CallSignalLimits
{
    /// <summary>Convite e resposta (SDP comprimido e perfil) cabem com folga.</summary>
    public const int MaxCodeLength = 16 * 1024;

    /// <summary>Id da chamada: 128 bits em hexadecimal minúsculo, como no código da chamada.</summary>
    public const int CallIdLength = 32;

    /// <summary>Toques seguidos por conexão antes de esperar.</summary>
    public const int RingBurst = 5;

    /// <summary>Um toque a mais liberado a cada tanto.</summary>
    public static readonly TimeSpan RingEvery = TimeSpan.FromSeconds(10);

    public static bool ValidCallId(string? callId) =>
        callId is { Length: CallIdLength } && callId.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}

/// <summary>Métodos que o app chama no hub.</summary>
public static class AppHubMethods
{
    public const string Typing = "Typing";

    /// <summary>Responde depois que a conexão já está inscrita nos grupos (ver AppHub.Ready).</summary>
    public const string Ready = "Ready";

    /// <summary>Sinal da chamada 1:1 pela conta; devolve se a outra pessoa está conectada.</summary>
    public const string DirectCall = "DirectCall";
}
