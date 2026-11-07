using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Desktop.Services;

public static class DesktopServices
{
    public static IServiceCollection AddDesktopServices(this IServiceCollection services, DesktopSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);

        var profiles = new ProfileStore(settings.ProfileFile);
        var titleBar = new TitleBarState();
        if (profiles.Current is { } profile)
        {
            titleBar.SetLocal(profile.TimeZone);
        }
        profiles.Changed += changed => titleBar.SetLocal(changed.TimeZone);
        services.AddSingleton(profiles);
        services.AddSingleton(titleBar);

        if (settings.Mode == ConnectionMode.Server)
        {
            services.AddSingleton<ISessionStore>(new DpapiSessionStore(settings.SessionFile));
            services.AddSingleton(new HttpClient { BaseAddress = settings.ServerUri, Timeout = TimeSpan.FromSeconds(15) });
            services.AddSingleton<AuthClient>();
            services.AddSingleton<RoomConnection>();
        }

        return services;
    }
}
