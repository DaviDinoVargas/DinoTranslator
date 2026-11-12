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
        services.AddSingleton(new EngineChoiceStore(settings.EngineChoiceFile));

        // Versão grátis: chamada por código, sem login.
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        // Fluxo aberto enquanto se espera a resposta: sem o limite de tempo padrão do HttpClient.
        var relayHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        relayHttp.DefaultRequestHeaders.UserAgent.ParseAdd("DinoTranslator");
        services.AddSingleton(new RendezvousRelay(relayHttp, settings.RendezvousUri));
        // Escopo do BlazorWebView: uma sessão de chamada por janela.
        services.AddScoped<CallSession>();

        // Versão Plus: conta no seu servidor, grupos, chat e chamadas em grupo.
        services.AddSingleton(new Plus.PlusServerStore(settings.PlusServerFile));
        services.AddSingleton<Plus.PlusHttp>();
        services.AddSingleton<ISessionStore>(new DpapiSessionStore(settings.SessionFile));
        services.AddSingleton<AuthClient>();
        services.AddSingleton<Plus.PlusApi>();
        services.AddSingleton<Plus.MessageTranslator>();
        services.AddSingleton<Plus.PlusState>();
        services.AddSingleton<Plus.IRelayProvider, Plus.PlusRelay>();
        services.AddSingleton<Plus.ICallSignaling>(provider => provider.GetRequiredService<Plus.PlusState>());
        services.AddScoped<Plus.DirectCalls>();
        services.AddSingleton<Plus.PlusGlossary>();
        services.AddSingleton<Translation.IGlossarySource>(provider => provider.GetRequiredService<Plus.PlusGlossary>());
        // A chamada em grupo usa o WebView2 da janela: uma por janela, como a chamada por código.
        services.AddScoped<Plus.GroupCall>();

        return services;
    }
}
