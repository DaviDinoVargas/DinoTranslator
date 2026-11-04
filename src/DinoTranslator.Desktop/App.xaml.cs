using System.Windows;
using DinoTranslator.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        MainWindow = new MainWindow(_services, settings);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
