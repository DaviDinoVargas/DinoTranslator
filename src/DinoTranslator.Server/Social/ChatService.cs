using DinoTranslator.Server.Data;
using DinoTranslator.Server.Realtime;
using DinoTranslator.Shared.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace DinoTranslator.Server.Social;

/// <summary>
/// Mensagens dos canais e das conversas diretas. Guarda o texto como foi escrito e o idioma de
/// quem escreveu; a tradução acontece no app de cada leitor.
/// </summary>
public sealed class ChatService(AppDbContext db, SocialAccess access, Notifier notifier, HybridCache cache, TimeProvider time)
{
    private static readonly HybridCacheEntryOptions AuthorEntry = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

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
        var messages = rows.Take(size).Select(m => ToInfo(m, Text.Summary(m.Author))).ToList();
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
        await db.Channels.Where(c => c.Id == channelId && c.LastMessageId < message.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.LastMessageId, message.Id), cancellationToken);

        var info = ToInfo(message, author);
        await notifier.MessageCreatedAsync(channel, info);
        return Outcome<MessageInfo>.Ok(info);
    }

    public async Task<Outcome<MessageInfo>> EditAsync(Guid userId, long messageId, EditMessageRequest? request, CancellationToken cancellationToken)
    {
        var message = await db.Messages.Include(m => m.Author).SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
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
        var info = ToInfo(message, Text.Summary(message.Author));
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
        await db.SaveChangesAsync(cancellationToken);
        await notifier.MessageDeletedAsync(readable.Channel, messageId);
        return Outcome<Done>.Ok(Done.Value);
    }

    private async Task<UserSummary> AuthorAsync(Guid userId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync($"autor:{userId:N}", async token =>
            await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => new UserSummary(u.Id, u.UserName, u.DisplayName, u.Language))
                .SingleAsync(token),
            AuthorEntry, cancellationToken: cancellationToken);

    private static MessageInfo ToInfo(Message message, UserSummary author) => new(
        message.Id,
        message.ChannelId,
        author,
        message.DeletedAtUtc is null ? message.Content : string.Empty,
        message.Language,
        Text.Utc(message.CreatedAtUtc),
        message.EditedAtUtc is { } edited ? Text.Utc(edited) : null,
        message.DeletedAtUtc is not null,
        message.ReplyToId);
}
