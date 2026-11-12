using DinoTranslator.Shared.Social;

namespace DinoTranslator.Server.Data;

public sealed class Group
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid OwnerId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Termo do glossário pessoal (dono = a conta) ou do grupo.</summary>
public sealed class GlossaryTerm
{
    public long Id { get; set; }

    public Guid? OwnerId { get; set; }

    public Guid? GroupId { get; set; }

    public required string Term { get; set; }

    public string? Translation { get; set; }

    public string? Language { get; set; }

    public int Position { get; set; }
}

public sealed class GroupMember
{
    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public GroupRole Role { get; set; }

    public DateTime JoinedAtUtc { get; set; }
}

/// <summary>Canal de texto ou de voz de um grupo, ou a conversa direta entre duas contas.</summary>
public sealed class Channel
{
    public Guid Id { get; set; }

    public Guid? GroupId { get; set; }

    public required string Name { get; set; }

    public ChannelKind Kind { get; set; }

    public int Position { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Conversa direta: as duas contas, a de menor id primeiro (par único).</summary>
    public Guid? DirectLowId { get; set; }

    public Guid? DirectHighId { get; set; }

    /// <summary>Id da última mensagem, para o app saber o que ainda não foi lido.</summary>
    public long LastMessageId { get; set; }
}

/// <summary>Mensagem como foi escrita, com o idioma de quem escreveu. Id crescente (ordem e paginação).</summary>
public sealed class Message
{
    public long Id { get; set; }

    public Guid ChannelId { get; set; }

    public Guid AuthorId { get; set; }

    public User Author { get; set; } = null!;

    public required string Content { get; set; }

    public required string Language { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? EditedAtUtc { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    public long? ReplyToId { get; set; }

    /// <summary>Fixada no canal (e quando).</summary>
    public DateTime? PinnedAtUtc { get; set; }
}

public sealed class MessageReaction
{
    public long MessageId { get; set; }

    public Guid UserId { get; set; }

    public required string Emoji { get; set; }
}

/// <summary>Quem foi mencionado com @usuário (só contas que podem ver o canal).</summary>
public sealed class MessageMention
{
    public long MessageId { get; set; }

    public Guid UserId { get; set; }
}

public sealed class GroupInvite
{
    /// <summary>10 caracteres aleatórios (50 bits): impossível de adivinhar no limite de tentativas.</summary>
    public required string Code { get; set; }

    public Guid GroupId { get; set; }

    public Guid CreatedById { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public int MaxUses { get; set; }

    public int Uses { get; set; }
}

public enum FriendshipStatus
{
    Pending = 0,
    Accepted = 1,
}

/// <summary>Amizade (ou pedido) entre duas contas, a de menor id primeiro.</summary>
public sealed class Friendship
{
    public Guid LowId { get; set; }

    public Guid HighId { get; set; }

    public Guid RequestedById { get; set; }

    public FriendshipStatus Status { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Quem bloqueou quem: sem pedidos de amizade nem conversa direta entre os dois.</summary>
public sealed class Block
{
    public Guid UserId { get; set; }

    public Guid BlockedId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public static class Pairs
{
    /// <summary>O par em ordem fixa, para amizades e conversas diretas terem uma linha só.</summary>
    public static (Guid Low, Guid High) Of(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
}
