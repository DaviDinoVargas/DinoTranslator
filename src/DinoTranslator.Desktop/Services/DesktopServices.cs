using System.Net.Http;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;
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

        services.AddSingleton(new TranslationSettingsStore(settings.TranslationFile));
        services.AddSingleton(new ConnectionSettingsStore(settings.ConnectionFile));
        services.AddSingleton(new SpeechEngineFactory(new ModelFolder(settings.ModelsFolder)));

        if (settings.Mode == ConnectionMode.Code)
        {
            services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
            // Fluxo aberto enquanto se espera a resposta: sem o limite de tempo padrão do HttpClient.
            var relayHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            relayHttp.DefaultRequestHeaders.UserAgent.ParseAdd("DinoTranslator");
            services.AddSingleton(new RendezvousRelay(relayHttp, settings.RendezvousUri));
            // Escopo do BlazorWebView: uma sessão de chamada por janela.
            services.AddScoped<CallSession>();
        }

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
