using System.Text.RegularExpressions;
using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Data;
using DinoTranslator.Server.Realtime;
using DinoTranslator.Shared.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace DinoTranslator.Server.Social;

/// <summary>
/// Mensagens dos canais e das conversas diretas, com reações, respostas, menções, fixadas e busca.
/// Guarda o texto como foi escrito e o idioma de quem escreveu; a tradução acontece no app de
/// cada leitor.
/// </summary>
public sealed partial class ChatService(AppDbContext db, SocialAccess access, Notifier notifier, HybridCache cache, TimeProvider time)
{
    private const int MaxMentions = 20;
    private const int QuoteLength = 200;
    private static readonly HybridCacheEntryOptions AuthorEntry = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

    [GeneratedRegex(@"(?<![\w.@])@([a-zA-Z0-9._-]{3,32})")]
    private static partial Regex MentionPattern();

    public async Task<Outcome<MessagePage>> PageAsync(Guid userId, Guid channelId, long? before, int? limit, CancellationToken cancellationToken)
    {
        if (await access.ReadableAsync(channelId, userId, cancellationToken) is null)
        {
            return Problem.NotFound;
        }
        var size = Math.Clamp(limit ?? SocialLimits.DefaultPageSize, 1, SocialLimits.MaxPageSize);
        var query = db.Messages.AsNoTracking().Where(m => m.ChannelId == channelId);
        if (before is { } id)
        {
            query = query.Where(m => m.Id < id);
        }
        var rows = await query.OrderByDescending(m => m.Id).Take(size + 1).Include(m => m.Author).ToListAsync(cancellationToken);
        var messages = await EnrichAsync(rows.Take(size).ToList(), cancellationToken);
        return Outcome<MessagePage>.Ok(new MessagePage(messages, rows.Count > size));
    }

    public async Task<Outcome<MessageInfo>> SendAsync(Guid userId, Guid channelId, SendMessageRequest? request, CancellationToken cancellationToken)
    {
        if (await access.ReadableAsync(channelId, userId, cancellationToken) is not { } readable)
        {
            return Problem.NotFound;
        }
        var channel = readable.Channel;
        if (channel.Kind == ChannelKind.Voice)
        {
            return Problem.Invalid("Canal de voz não tem mensagens.");
        }
        if (Text.CleanMessage(request?.Content) is not { } content)
        {
            return Problem.Invalid($"A mensagem precisa ter de 1 a {SocialLimits.MaxMessageLength} caracteres.");
        }
        if (channel.IsDirect && await access.BlockedAsync(userId, channel.OtherDirectUser(userId)!.Value, cancellationToken))
        {
            return Problem.Forbidden("Não é possível mandar mensagem para esta pessoa.");
        }
        if (request!.ReplyToId is { } replyTo && !await db.Messages.AnyAsync(m => m.Id == replyTo && m.ChannelId == channelId, cancellationToken))
        {
            return Problem.Invalid("A mensagem respondida não existe neste canal.");
        }

        var author = await AuthorAsync(userId, cancellationToken);
        var message = new Message
        {
            ChannelId = channelId,
            AuthorId = userId,
            Content = content,
            Language = author.Language,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            ReplyToId = request.ReplyToId,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        await SaveMentionsAsync(message, channel, cancellationToken);
        await db.Channels.Where(c => c.Id == channelId && c.LastMessageId < message.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.LastMessageId, message.Id), cancellationToken);

        var info = await InfoAsync(message.Id, cancellationToken);
        await notifier.MessageCreatedAsync(channel, info);
        return Outcome<MessageInfo>.Ok(info);
    }

    public async Task<Outcome<MessageInfo>> EditAsync(Guid userId, long messageId, EditMessageRequest? request, CancellationToken cancellationToken)
    {
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.DeletedAtUtc is not null || await access.ReadableAsync(message.ChannelId, userId, cancellationToken) is not { } readable)
        {
            return Problem.NotFound;
        }
        if (message.AuthorId != userId)
        {
            return Problem.Forbidden("Só quem escreveu edita a mensagem.");
        }
        if (Text.CleanMessage(request?.Content) is not { } content)
        {
            return Problem.Invalid($"A mensagem precisa ter de 1 a {SocialLimits.MaxMessageLength} caracteres.");
        }
        message.Content = content;
        message.EditedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await db.MessageMentions.Where(m => m.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);
        await SaveMentionsAsync(message, readable.Channel, cancellationToken);
        var info = await InfoAsync(messageId, cancellationToken);
        await notifier.MessageEditedAsync(readable.Channel, info);
        return Outcome<MessageInfo>.Ok(info);
    }

    /// <summary>Quem escreveu, ou um administrador do grupo. O texto some de vez; fica só o aviso.</summary>
    public async Task<Outcome<Done>> DeleteAsync(Guid userId, long messageId, CancellationToken cancellationToken)
    {
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.DeletedAtUtc is not null || await access.ReadableAsync(message.ChannelId, userId, cancellationToken) is not { } readable)
        {
            return Problem.NotFound;
        }
        if (message.AuthorId != userId && !(readable.Role >= GroupRole.Admin))
        {
            return Problem.Forbidden();
        }
        message.Content = string.Empty;
        message.DeletedAtUtc = time.GetUtcNow().UtcDateTime;
        message.PinnedAtUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        await db.MessageReactions.Where(r => r.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);
        await db.MessageMentions.Where(m => m.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);
        await notifier.MessageDeletedAsync(readable.Channel, messageId);
        return Outcome<Done>.Ok(Done.Value);
    }

    /// <summary>Reação rápida (lista fixa de emojis): põe ou tira a da própria pessoa.</summary>
    public async Task<Outcome<IReadOnlyList<ReactionInfo>>> ReactAsync(Guid userId, long messageId, string? emoji, bool add, CancellationToken cancellationToken)
    {
        if (emoji is null || !SocialLimits.Reactions.Contains(emoji))
        {
            return Problem.Invalid("Reação inválida.");
        }
        var message = await db.Messages.AsNoTracking().Where(m => m.Id == messageId).Select(m => new { m.ChannelId, m.DeletedAtUtc }).SingleOrDefaultAsync(cancellationToken);
        if (message is null || message.DeletedAtUtc is not null || await access.ReadableAsync(message.ChannelId, userId, cancellationToken) is not { } readable)
        {
            return Problem.NotFound;
        }
        if (add)
        {
            if (!await db.MessageReactions.AnyAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji, cancellationToken))
            {
                db.MessageReactions.Add(new MessageReaction { MessageId = messageId, UserId = userId, Emoji = emoji });
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Dois cliques ao mesmo tempo: a reação já está lá.
                }
            }
        }
        else
        {
            await db.MessageReactions.Where(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji).ExecuteDeleteAsync(cancellationToken);
        }
        var reactions = (await ReactionsAsync([messageId], cancellationToken)).GetValueOrDefault(messageId) ?? [];
        await notifier.ReactionsChangedAsync(readable.Channel, messageId, reactions);
        return Outcome<IReadOnlyList<ReactionInfo>>.Ok(reactions);
    }

    /// <summary>Fixa ou solta: num grupo, administradores; na conversa direta, qualquer das duas pessoas.</summary>
    public async Task<Outcome<MessageInfo>> PinAsync(Guid userId, long messageId, bool pin, CancellationToken cancellationToken)
    {
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.DeletedAtUtc is not null || await access.ReadableAsync(message.ChannelId, userId, cancellationToken) is not { } readable)
        {
            return Problem.NotFound;
        }
        if (!readable.Channel.IsDirect && readable.Role < GroupRole.Admin)
        {
            return Problem.Forbidden("Só administradores fixam mensagens no grupo.");
        }
        if (pin && message.PinnedAtUtc is null
            && await db.Messages.CountAsync(m => m.ChannelId == message.ChannelId && m.PinnedAtUtc != null, cancellationToken) >= SocialLimits.MaxPinsPerChannel)
        {
            return Problem.Invalid($"O canal já tem {SocialLimits.MaxPinsPerChannel} mensagens fixadas, o máximo.");
        }
        message.PinnedAtUtc = pin ? message.PinnedAtUtc ?? time.GetUtcNow().UtcDateTime : null;
        await db.SaveChangesAsync(cancellationToken);
        var info = await InfoAsync(messageId, cancellationToken);
        await notifier.MessageEditedAsync(readable.Channel, info);
        return Outcome<MessageInfo>.Ok(info);
    }

    public async Task<Outcome<List<MessageInfo>>> PinsAsync(Guid userId, Guid channelId, CancellationToken cancellationToken)
    {
        if (await access.ReadableAsync(channelId, userId, cancellationToken) is null)
        {
            return Problem.NotFound;
        }
        var rows = await db.Messages.AsNoTracking().Where(m => m.ChannelId == channelId && m.PinnedAtUtc != null)
            .OrderByDescending(m => m.PinnedAtUtc).Include(m => m.Author).ToListAsync(cancellationToken);
        return Outcome<List<MessageInfo>>.Ok(await EnrichAsync(rows, cancellationToken));
    }

    /// <summary>
    /// Busca no texto das mensagens de tudo o que a pessoa pode ler (ou só de um grupo ou canal).
    /// No PostgreSQL usa o índice de trigramas, que deixa a busca por trecho rápida mesmo com muitas mensagens.
    /// </summary>
    public async Task<Outcome<List<SearchResult>>> SearchAsync(Guid userId, string? query, Guid? channelId, Guid? groupId, CancellationToken cancellationToken)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length is < SocialLimits.MinSearchLength or > SocialLimits.MaxSearchLength)
        {
            return Problem.Invalid($"Busque de {SocialLimits.MinSearchLength} a {SocialLimits.MaxSearchLength} caracteres.");
        }
        var myGroups = db.GroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId);
        var channels = db.Channels.Where(c => (c.GroupId != null && myGroups.Contains(c.GroupId.Value)) || c.DirectLowId == userId || c.DirectHighId == userId);
        if (groupId is { } group)
        {
            channels = channels.Where(c => c.GroupId == group);
        }
        if (channelId is { } channel)
        {
            channels = channels.Where(c => c.Id == channel);
        }
        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var matches = db.Messages.AsNoTracking()
            .Where(m => m.DeletedAtUtc == null && channels.Select(c => c.Id).Contains(m.ChannelId));
        matches = db.Database.IsNpgsql()
            ? matches.Where(m => EF.Functions.ILike(m.Content, pattern, "\\"))
            : matches.Where(m => EF.Functions.Like(m.Content, pattern, "\\"));
        var rows = await matches.OrderByDescending(m => m.Id).Take(SocialLimits.MaxSearchResults).Include(m => m.Author).ToListAsync(cancellationToken);

        var channelIds = rows.Select(r => r.ChannelId).Distinct().ToList();
        var places = await db.Channels.AsNoTracking().Where(c => channelIds.Contains(c.Id))
            .Select(c => new { c.Id, c.GroupId, c.Name, c.DirectLowId, c.DirectHighId }).ToListAsync(cancellationToken);
        var others = places.Where(p => p.GroupId is null).Select(p => p.DirectLowId == userId ? p.DirectHighId : p.DirectLowId).OfType<Guid>().ToList();
        var otherNames = await db.Users.AsNoTracking().Where(u => others.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var infos = await EnrichAsync(rows, cancellationToken);
        return Outcome<List<SearchResult>>.Ok([.. infos.Select(info =>
        {
            var place = places.Single(p => p.Id == info.ChannelId);
            var name = place.GroupId is not null
                ? "#" + place.Name
                : otherNames.GetValueOrDefault((place.DirectLowId == userId ? place.DirectHighId : place.DirectLowId) ?? Guid.Empty, "Conversa");
            return new SearchResult(info, place.GroupId, name);
        })]);
    }

    /// <summary>@usuário de quem pode ver o canal vira menção; o resto fica só como texto.</summary>
    private async Task SaveMentionsAsync(Message message, ChannelRef channel, CancellationToken cancellationToken)
    {
        var names = MentionPattern().Matches(message.Content).Select(m => Credentials.NormalizeUserName(m.Groups[1].Value)).Distinct().Take(MaxMentions).ToList();
        if (names.Count == 0)
        {
            return;
        }
        var users = db.Users.AsNoTracking().Where(u => names.Contains(u.UserName) && u.Id != message.AuthorId);
        var mentioned = channel.IsDirect
            ? await users.Where(u => u.Id == channel.DirectLowId || u.Id == channel.DirectHighId).Select(u => u.Id).ToListAsync(cancellationToken)
            : await users.Where(u => db.GroupMembers.Any(m => m.GroupId == channel.GroupId && m.UserId == u.Id)).Select(u => u.Id).ToListAsync(cancellationToken);
        if (mentioned.Count == 0)
        {
            return;
        }
        db.MessageMentions.AddRange(mentioned.Select(id => new MessageMention { MessageId = message.Id, UserId = id }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<MessageInfo> InfoAsync(long messageId, CancellationToken cancellationToken)
    {
        var row = await db.Messages.AsNoTracking().Include(m => m.Author).SingleAsync(m => m.Id == messageId, cancellationToken);
        return (await EnrichAsync([row], cancellationToken))[0];
    }

    /// <summary>Reações, menções e citações das mensagens, em poucas consultas para a página toda.</summary>
    private async Task<List<MessageInfo>> EnrichAsync(List<Message> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var reactions = await ReactionsAsync(ids, cancellationToken);
        var mentions = (await db.MessageMentions.AsNoTracking().Where(m => ids.Contains(m.MessageId)).ToListAsync(cancellationToken))
            .GroupBy(m => m.MessageId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)[.. g.Select(m => m.UserId)]);
        var replyIds = rows.Where(r => r.ReplyToId is not null).Select(r => r.ReplyToId!.Value).Distinct().ToList();
        var replies = replyIds.Count == 0 ? [] : await db.Messages.AsNoTracking().Where(m => replyIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Author.DisplayName, m.Content, m.Language, Deleted = m.DeletedAtUtc != null })
            .ToDictionaryAsync(m => m.Id, cancellationToken);
        return [.. rows.Select(m =>
        {
            var deleted = m.DeletedAtUtc is not null;
            MessageQuote? quote = m.ReplyToId is { } replyId && replies.TryGetValue(replyId, out var reply)
                ? new MessageQuote(reply.Id, reply.DisplayName, reply.Deleted ? string.Empty : Shorten(reply.Content), reply.Language, reply.Deleted)
                : null;
            return new MessageInfo(
                m.Id,
                m.ChannelId,
                Text.Summary(m.Author),
                deleted ? string.Empty : m.Content,
                m.Language,
                Text.Utc(m.CreatedAtUtc),
                m.EditedAtUtc is { } edited ? Text.Utc(edited) : null,
                deleted,
                m.ReplyToId,
                deleted ? [] : reactions.GetValueOrDefault(m.Id) ?? [],
                mentions.GetValueOrDefault(m.Id) ?? [],
                !deleted && m.PinnedAtUtc is not null,
                quote);
        })];
    }

    private async Task<Dictionary<long, IReadOnlyList<ReactionInfo>>> ReactionsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        var rows = await db.MessageReactions.AsNoTracking().Where(r => ids.Contains(r.MessageId)).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.MessageId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ReactionInfo>)[.. g.GroupBy(r => r.Emoji)
                .OrderBy(e => SocialLimits.Reactions.ToList().IndexOf(e.Key))
                .Select(e => new ReactionInfo(e.Key, [.. e.Select(r => r.UserId)]))]);
    }

    private static string Shorten(string content) => content.Length <= QuoteLength ? content : content[..QuoteLength] + "…";

    private async Task<UserSummary> AuthorAsync(Guid userId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync($"autor:{userId:N}", async token =>
            await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => new UserSummary(u.Id, u.UserName, u.DisplayName, u.Language))
                .SingleAsync(token),
            AuthorEntry, cancellationToken: cancellationToken);
}
