using System.Security.Claims;
using System.Threading.RateLimiting;
using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Infrastructure;
using DinoTranslator.Server.Realtime;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.RateLimiting;

namespace DinoTranslator.Server.Social;

public static class SocialSetup
{
    public const string MessageRateLimit = "mensagens";
    public const string ActionRateLimit = "acoes";

    public static IServiceCollection AddSocial(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<SocialAccess>();
        services.AddScoped<GroupService>();
        services.AddScoped<ChatService>();
        services.AddScoped<FriendService>();
        services.AddScoped<GlossaryService>();
        services.AddSingleton<Notifier>();
        if (Scaling.HasRedis(configuration))
        {
            services.AddSingleton<RedisPresenceStore>();
            services.AddSingleton<IPresenceStore>(provider => provider.GetRequiredService<RedisPresenceStore>());
            services.AddHostedService<PresenceHeartbeat>();
        }
        else
        {
            services.AddSingleton<IPresenceStore, MemoryPresenceStore>();
        }

        // Por conta (não por IP): 10 mensagens a cada 10 s e 60 ações (criar, convidar, pedir
        // amizade...) por minuto. Cada instância conta as suas.
        services.Configure<RateLimiterOptions>(limiter =>
        {
            limiter.AddPolicy(MessageRateLimit, context => RateLimitPartition.GetSlidingWindowLimiter(
                "m:" + UserKey(context), _ => new SlidingWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromSeconds(10), SegmentsPerWindow = 5, QueueLimit = 0 }));
            limiter.AddPolicy(ActionRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                "a:" + UserKey(context), _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }

    private static string UserKey(HttpContext context) =>
        context.User.GetUserId()?.ToString() ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonimo";

    public static IEndpointRouteBuilder MapSocial(this IEndpointRouteBuilder app)
    {
        app.MapHub<AppHub>(SocialRoutes.Hub);

        var api = app.MapGroup(string.Empty).RequireAuthorization(AuthClaims.PlusPolicy);
        static Guid Me(ClaimsPrincipal user) => user.GetUserId() ?? throw new InvalidOperationException("Sem usuário.");

        // Grupos
        api.MapGet(SocialRoutes.MyGroups, async (ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            Results.Ok(await groups.MineAsync(Me(user), ct)));
        api.MapPost(SocialRoutes.Groups, async (NameRequest request, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.CreateAsync(Me(user), request?.Name, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapGet("/api/grupos/{groupId:guid}", async (Guid groupId, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.DetailsAsync(Me(user), groupId, ct)).ToResult());
        api.MapPatch("/api/grupos/{groupId:guid}", async (Guid groupId, NameRequest request, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.RenameAsync(Me(user), groupId, request?.Name, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapDelete("/api/grupos/{groupId:guid}", async (Guid groupId, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.DeleteAsync(Me(user), groupId, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapPost("/api/grupos/{groupId:guid}/canais", async (Guid groupId, CreateChannelRequest request, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.CreateChannelAsync(Me(user), groupId, request, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapDelete("/api/canais/{channelId:guid}", async (Guid channelId, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.DeleteChannelAsync(Me(user), channelId, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapPost("/api/grupos/{groupId:guid}/convites", async (Guid groupId, CreateInviteRequest? request, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.CreateInviteAsync(Me(user), groupId, request, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapGet("/api/convites/{code}", async (string code, GroupService groups, CancellationToken ct) =>
            (await groups.PreviewAsync(code, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapPost("/api/convites/{code}/entrar", async (string code, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.JoinAsync(Me(user), code, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapDelete("/api/grupos/{groupId:guid}/membros/{userId:guid}", async (Guid groupId, Guid userId, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.RemoveMemberAsync(Me(user), groupId, userId, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapPut("/api/grupos/{groupId:guid}/membros/{userId:guid}/papel", async (Guid groupId, Guid userId, RoleRequest request, ClaimsPrincipal user, GroupService groups, CancellationToken ct) =>
            (await groups.SetRoleAsync(Me(user), groupId, userId, request.Role, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);

        // Mensagens
        api.MapGet("/api/canais/{channelId:guid}/mensagens", async (Guid channelId, long? antes, int? limite, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.PageAsync(Me(user), channelId, antes, limite, ct)).ToResult());
        api.MapPost("/api/canais/{channelId:guid}/mensagens", async (Guid channelId, SendMessageRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.SendAsync(Me(user), channelId, request, ct)).ToResult()).RequireRateLimiting(MessageRateLimit);
        api.MapPatch("/api/mensagens/{messageId:long}", async (long messageId, EditMessageRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.EditAsync(Me(user), messageId, request, ct)).ToResult()).RequireRateLimiting(MessageRateLimit);
        api.MapDelete("/api/mensagens/{messageId:long}", async (long messageId, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.DeleteAsync(Me(user), messageId, ct)).ToResult()).RequireRateLimiting(MessageRateLimit);
        api.MapPut("/api/mensagens/{messageId:long}/reacoes/{emoji}", async (long messageId, string emoji, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.ReactAsync(Me(user), messageId, Uri.UnescapeDataString(emoji), add: true, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapDelete("/api/mensagens/{messageId:long}/reacoes/{emoji}", async (long messageId, string emoji, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.ReactAsync(Me(user), messageId, Uri.UnescapeDataString(emoji), add: false, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapPut("/api/mensagens/{messageId:long}/fixar", async (long messageId, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.PinAsync(Me(user), messageId, pin: true, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapDelete("/api/mensagens/{messageId:long}/fixar", async (long messageId, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.PinAsync(Me(user), messageId, pin: false, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapGet("/api/canais/{channelId:guid}/fixadas", async (Guid channelId, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.PinsAsync(Me(user), channelId, ct)).ToResult());
        api.MapGet(SocialRoutes.Search, async (string? q, Guid? canal, Guid? grupo, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            (await chat.SearchAsync(Me(user), q, canal, grupo, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);

        // Amigos, bloqueios e conversas diretas
        api.MapGet(SocialRoutes.Friends, async (ClaimsPrincipal user, FriendService friends, CancellationToken ct) =>
            Results.Ok(await friends.ListAsync(Me(user), ct)));
        api.MapPost(SocialRoutes.Friends, async (FriendRequest request, ClaimsPrincipal user, FriendService friends, CancellationToken ct) =>
            (await friends.RequestAsync(Me(user), request?.UserName, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapDelete("/api/amigos/{userId:guid}", async (Guid userId, ClaimsPrincipal user, FriendService friends, CancellationToken ct) =>
            (await friends.RemoveAsync(Me(user), userId, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapPost("/api/bloqueios/{userId:guid}", async (Guid userId, ClaimsPrincipal user, FriendService friends, CancellationToken ct) =>
            (await friends.BlockAsync(Me(user), userId, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapDelete("/api/bloqueios/{userId:guid}", async (Guid userId, ClaimsPrincipal user, FriendService friends, CancellationToken ct) =>
            (await friends.UnblockAsync(Me(user), userId, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapGet(SocialRoutes.Directs, async (ClaimsPrincipal user, FriendService friends, CancellationToken ct) =>
            Results.Ok(await friends.DirectsAsync(Me(user), ct)));
        api.MapPost("/api/conversas/{userId:guid}", async (Guid userId, ClaimsPrincipal user, FriendService friends, CancellationToken ct) =>
            (await friends.OpenDirectAsync(Me(user), userId, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);

        // Glossário
        api.MapGet(SocialRoutes.Glossary, async (ClaimsPrincipal user, GlossaryService glossary, CancellationToken ct) =>
            (await glossary.PersonalAsync(Me(user), ct)).ToResult());
        api.MapPut(SocialRoutes.Glossary, async (GlossaryRequest request, ClaimsPrincipal user, GlossaryService glossary, CancellationToken ct) =>
            (await glossary.SetPersonalAsync(Me(user), request, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        api.MapGet("/api/grupos/{groupId:guid}/glossario", async (Guid groupId, ClaimsPrincipal user, GlossaryService glossary, CancellationToken ct) =>
            (await glossary.GroupAsync(Me(user), groupId, ct)).ToResult());
        api.MapPut("/api/grupos/{groupId:guid}/glossario", async (Guid groupId, GlossaryRequest request, ClaimsPrincipal user, GlossaryService glossary, CancellationToken ct) =>
            (await glossary.SetGroupAsync(Me(user), groupId, request, ct)).ToResult()).RequireRateLimiting(ActionRateLimit);
        return app;
    }
}
