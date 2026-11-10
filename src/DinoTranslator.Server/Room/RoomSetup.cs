using DinoTranslator.Server.Auth;
using DinoTranslator.Shared.Room;
using DinoTranslator.Server.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace DinoTranslator.Server.Room;

public static class RoomSetup
{
    public static IServiceCollection AddRoom(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 64 * 1024;
            options.EnableDetailedErrors = false;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            options.AddFilter<HubRateLimitFilter>();
        }).AddScaleOut(configuration);
        return services;
    }

    /// <summary>Endereça mensagens pelo id do usuário (claim "sub" do JWT).</summary>
    private sealed class SubjectUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection) => connection.User.GetUserId()?.ToString();
    }
}
