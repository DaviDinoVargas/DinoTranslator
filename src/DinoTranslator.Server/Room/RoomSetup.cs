using DinoTranslator.Server.Auth;
using DinoTranslator.Shared.Room;
using Microsoft.AspNetCore.SignalR;

namespace DinoTranslator.Server.Room;

public static class RoomSetup
{
    public static IServiceCollection AddRoom(this IServiceCollection services)
    {
        services.AddSingleton<PresenceTracker>();
        services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 64 * 1024;
            options.EnableDetailedErrors = false;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            options.AddFilter<HubRateLimitFilter>();
        });
        return services;
    }

    public static IEndpointRouteBuilder MapRoom(this IEndpointRouteBuilder app)
    {
        app.MapHub<RoomHub>(RoomRoutes.Hub);
        return app;
    }

    /// <summary>Endereça mensagens pelo id do usuário (claim "sub" do JWT).</summary>
    private sealed class SubjectUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection) => connection.User.GetUserId()?.ToString();
    }
}
