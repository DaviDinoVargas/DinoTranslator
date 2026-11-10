using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Data;
using DinoTranslator.Server.Infrastructure;
using DinoTranslator.Server.Realtime;
using DinoTranslator.Server.Social;
using DinoTranslator.Shared.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace DinoTranslator.Server.Calls;

/// <summary>
/// Servidor de mídia (LiveKit, SFU) das chamadas em grupo. Ele roda junto com a API e só aceita
/// quem chega com uma permissão assinada por ela, para um canal que a pessoa pode usar.
/// </summary>
public sealed class LiveKitOptions
{
    public const string Section = "LiveKit";

    /// <summary>Endereço que o app usa para conectar (wss://…).</summary>
    public string? Url { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Segredo compartilhado com o LiveKit (32+ caracteres): assina as permissões e os webhooks.</summary>
    public string? ApiSecret { get; set; }

    public int TokenHours { get; set; } = 6;

    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey) && ApiSecret is { Length: >= 32 };
}

/// <summary>Quem está em cada canal de voz, segundo os webhooks do LiveKit.</summary>
public interface IVoiceStateStore
{
    Task<IReadOnlyList<Guid>> JoinAsync(Guid channelId, Guid userId);

    Task<IReadOnlyList<Guid>> LeaveAsync(Guid channelId, Guid userId);

    Task ClearAsync(Guid channelId);

    Task<IReadOnlyList<Guid>> MembersAsync(Guid channelId);
}

public sealed class MemoryVoiceStateStore : IVoiceStateStore
{
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _rooms = new();

    public Task<IReadOnlyList<Guid>> JoinAsync(Guid channelId, Guid userId) => Change(channelId, set => set.Add(userId));

    public Task<IReadOnlyList<Guid>> LeaveAsync(Guid channelId, Guid userId) => Change(channelId, set => set.Remove(userId));

    public Task ClearAsync(Guid channelId)
    {
        _rooms.TryRemove(channelId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> MembersAsync(Guid channelId) => Change(channelId, _ => { });

    private Task<IReadOnlyList<Guid>> Change(Guid channelId, Action<HashSet<Guid>> change)
    {
        var set = _rooms.GetOrAdd(channelId, _ => []);
        lock (set)
        {
            change(set);
            return Task.FromResult<IReadOnlyList<Guid>>([.. set]);
        }
    }
}

public sealed class RedisVoiceStateStore(IConnectionMultiplexer redis) : IVoiceStateStore
{
    private static RedisKey Key(Guid channelId) => $"dino:voz:{channelId:N}";

    public async Task<IReadOnlyList<Guid>> JoinAsync(Guid channelId, Guid userId)
    {
        var db = redis.GetDatabase();
        await db.SetAddAsync(Key(channelId), userId.ToString("N"));
        // Uma sala esquecida (LiveKit reiniciado sem avisar) some sozinha.
        await db.KeyExpireAsync(Key(channelId), TimeSpan.FromHours(12));
        return await MembersAsync(channelId);
    }

    public async Task<IReadOnlyList<Guid>> LeaveAsync(Guid channelId, Guid userId)
    {
        await redis.GetDatabase().SetRemoveAsync(Key(channelId), userId.ToString("N"));
        return await MembersAsync(channelId);
    }

    public Task ClearAsync(Guid channelId) => redis.GetDatabase().KeyDeleteAsync(Key(channelId));

    public async Task<IReadOnlyList<Guid>> MembersAsync(Guid channelId) =>
        [.. (await redis.GetDatabase().SetMembersAsync(Key(channelId))).Select(v => Guid.TryParse(v.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];
}

/// <summary>Permissões para entrar na sala do canal e leitura dos webhooks do LiveKit.</summary>
public sealed class VoiceService(
    IOptions<LiveKitOptions> options,
    SocialAccess access,
    AppDbContext db,
    IVoiceStateStore voices,
    Notifier notifier,
    TimeProvider time,
    ILogger<VoiceService> logger)
{
    private const string RoomPrefix = "canal-";

    private readonly JsonWebTokenHandler _handler = new();

    public static string RoomName(Guid channelId) => RoomPrefix + channelId.ToString("N");

    public async Task<Outcome<VoiceJoin>> JoinAsync(Guid userId, Guid channelId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Configured)
        {
            return new Problem(StatusCodes.Status503ServiceUnavailable, "As chamadas em grupo não estão configuradas neste servidor.");
        }
        if (await access.ReadableAsync(channelId, userId, cancellationToken) is not { } readable)
        {
            return Problem.NotFound;
        }
        var channel = readable.Channel;
        if (channel.Kind == ChannelKind.Text)
        {
            return Problem.Invalid("Canal de texto não tem chamada.");
        }
        if (channel.IsDirect && await access.BlockedAsync(userId, channel.OtherDirectUser(userId)!.Value, cancellationToken))
        {
            return Problem.Forbidden("Não é possível ligar para esta pessoa.");
        }
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.UserName, u.Language })
            .SingleAsync(cancellationToken);

        var now = time.GetUtcNow();
        var expires = now.AddHours(Math.Clamp(settings.TokenHours, 1, 24));
        var room = RoomName(channelId);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.ApiKey,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString("N"),
                ["name"] = user.DisplayName,
                // O idioma vai junto: cada app sabe para quais idiomas traduzir a própria fala.
                ["metadata"] = JsonSerializer.Serialize(new { lang = user.Language, user = user.UserName }),
                ["video"] = new Dictionary<string, object>
                {
                    ["room"] = room,
                    ["roomJoin"] = true,
                    ["canPublish"] = true,
                    ["canSubscribe"] = true,
                    ["canPublishData"] = true,
                },
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.ApiSecret!)), SecurityAlgorithms.HmacSha256),
        });
        return Outcome<VoiceJoin>.Ok(new VoiceJoin(settings.Url!, token, room, expires));
    }

    public async Task<Outcome<IReadOnlyList<Guid>>> MembersAsync(Guid userId, Guid channelId, CancellationToken cancellationToken) =>
        await access.ReadableAsync(channelId, userId, cancellationToken) is null
            ? Problem.NotFound
            : Outcome<IReadOnlyList<Guid>>.Ok(await voices.MembersAsync(channelId));

    /// <summary>
    /// Webhook do LiveKit: o cabeçalho Authorization traz um JWT assinado com o segredo, com o
    /// SHA-256 do corpo. Qualquer coisa fora disso é recusada.
    /// </summary>
    public async Task<bool> WebhookAsync(string? authorization, byte[] body)
    {
        var settings = options.Value;
        if (!settings.Configured || string.IsNullOrWhiteSpace(authorization))
        {
            return false;
        }
        var validation = await _handler.ValidateTokenAsync(authorization.Replace("Bearer ", string.Empty, StringComparison.Ordinal), new TokenValidationParameters
        {
            ValidIssuer = settings.ApiKey,
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.ApiSecret!)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
        });
        var expected = Convert.ToBase64String(SHA256.HashData(body));
        if (!validation.IsValid || !validation.Claims.TryGetValue("sha256", out var claimed) || claimed is not string hash
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(expected)))
        {
            logger.LogWarning("Webhook do LiveKit recusado.");
            return false;
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var kind = root.TryGetProperty("event", out var e) ? e.GetString() : null;
        var roomName = root.TryGetProperty("room", out var r) && r.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (roomName is null || !roomName.StartsWith(RoomPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(roomName[RoomPrefix.Length..], "N", out var channelId))
        {
            return true;
        }
        Guid? participant = root.TryGetProperty("participant", out var p) && p.TryGetProperty("identity", out var identity)
            && Guid.TryParseExact(identity.GetString(), "N", out var id) ? id : null;

        IReadOnlyList<Guid> members;
        switch (kind)
        {
            case "participant_joined" when participant is { } joined:
                members = await voices.JoinAsync(channelId, joined);
                break;
            case "participant_left" when participant is { } left:
                members = await voices.LeaveAsync(channelId, left);
                break;
            case "room_finished":
                await voices.ClearAsync(channelId);
                members = [];
                break;
            default:
                return true;
        }
        await NotifyAsync(channelId, members);
        return true;
    }

    private async Task NotifyAsync(Guid channelId, IReadOnlyList<Guid> members)
    {
        if (await access.ChannelAsync(channelId, CancellationToken.None) is not { } channel)
        {
            return;
        }
        if (channel.IsDirect)
        {
            await notifier.DirectVoiceChangedAsync(channel, members);
        }
        else
        {
            await notifier.VoiceChangedAsync(channel.GroupId!.Value, channelId, members);
        }
    }
}

public static class VoiceSetup
{
    public static IServiceCollection AddVoice(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LiveKitOptions>().BindConfiguration(LiveKitOptions.Section)
            .Validate(o => string.IsNullOrWhiteSpace(o.ApiSecret) || o.ApiSecret.Length >= 32, "LiveKit:ApiSecret precisa ter 32+ caracteres.");
        services.AddScoped<VoiceService>();
        if (Scaling.HasRedis(configuration))
        {
            services.AddSingleton<IVoiceStateStore, RedisVoiceStateStore>();
        }
        else
        {
            services.AddSingleton<IVoiceStateStore, MemoryVoiceStateStore>();
        }
        return services;
    }

    public static IEndpointRouteBuilder MapVoice(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/canais/{channelId:guid}/voz", async (Guid channelId, ClaimsPrincipal user, VoiceService voice, CancellationToken ct) =>
            (await voice.JoinAsync(user.GetUserId()!.Value, channelId, ct)).ToResult())
            .RequireAuthorization(AuthClaims.PlusPolicy).RequireRateLimiting(SocialSetup.ActionRateLimit);
        app.MapGet("/api/canais/{channelId:guid}/voz", async (Guid channelId, ClaimsPrincipal user, VoiceService voice, CancellationToken ct) =>
            (await voice.MembersAsync(user.GetUserId()!.Value, channelId, ct)).ToResult())
            .RequireAuthorization(AuthClaims.PlusPolicy);
        app.MapPost(SocialRoutes.LiveKitWebhook, async (HttpRequest request, VoiceService voice) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            return await voice.WebhookAsync(request.Headers.Authorization.ToString(), buffer.ToArray()) ? Results.Ok() : Results.Unauthorized();
        });
        return app;
    }
}
