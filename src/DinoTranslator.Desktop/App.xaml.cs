using System.Windows;
using DinoTranslator.Desktop.Services;
using DinoTranslator.Installation;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WebViewSecurity.ClearEnvironmentOverrides();

        // Aberto pelo Desinstalar.exe ou por "Aplicativos instalados" do Windows.
        if (e.Args is [Installer.UninstallArgument] || Installer.IsUninstaller(Environment.ProcessPath))
        {
            MainWindow = new UninstallWindow();
            MainWindow.Show();
            return;
        }

        DesktopSettings settings;
        try
        {
            settings = DesktopSettings.Load();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "DinoTranslator", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddDesktopServices(settings);
        _services = services.BuildServiceProvider();
        UseBundledModels(_services);

        MainWindow = new MainWindow(_services, settings);
        MainWindow.Show();
        RepairInstallation();
    }

    /// <summary>
    /// Modelos trazidos pelo instalador (na pasta dos extras) e tradução ainda desligada: começa com
    /// o motor local, sem a pessoa precisar baixar nada de novo.
    /// </summary>
    private static void UseBundledModels(IServiceProvider services)
    {
        var store = services.GetRequiredService<Services.Translation.TranslationSettingsStore>();
        if (store.Current.IsConfigured)
        {
            return;
        }
        var local = new Services.Translation.TranslationSettings(
            Services.Translation.TranslationProvider.Local, null, null, Services.Translation.Local.WhisperModel.Turbo);
        if (services.GetRequiredService<Services.Translation.SpeechEngineFactory>().IsReady(local))
        {
            store.Save(local);
        }
    }

    /// <summary>
    /// Instalações antigas (ou com o registro apagado) ganham o Desinstalar.exe e voltam para
    /// "Aplicativos instalados". Fora da janela e sem nunca atrapalhar a abertura do app.
    /// </summary>
    private static void RepairInstallation()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = Task.Run(() =>
        {
            try
            {
                Installer.Repair(AppContext.BaseDirectory, version);
            }
            catch (Exception)
            {
                // Conserto é melhor esforço: o app funciona igual sem ele.
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
