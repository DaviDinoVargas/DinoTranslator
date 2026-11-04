using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using DinoTranslator.Desktop.Components;
using DinoTranslator.Desktop.Services;
using Microsoft.AspNetCore.Components.WebView.Wpf;

namespace DinoTranslator.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeZoneInfo Brazil = FindZone("America/Sao_Paulo", "E. South America Standard Time");
    private static readonly TimeZoneInfo Moscow = FindZone("Europe/Moscow", "Russian Standard Time");

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public MainWindow(IServiceProvider services, DesktopSettings settings)
    {
        InitializeComponent();

        var webView = new BlazorWebView { HostPage = @"wwwroot\index.html", Services = services };
        webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        // Fora da pasta do programa: em "Arquivos de Programas" o usuário não tem permissão de escrita.
        webView.BlazorWebViewInitializing += (_, e) => e.UserDataFolder = settings.WebViewDataFolder;
        Host.Children.Add(webView);

        UpdateClocks();
        _clockTimer.Tick += (_, _) => UpdateClocks();
        _clockTimer.Start();
        StateChanged += (_, _) => OnWindowStateChanged();
        Closed += (_, _) => _clockTimer.Stop();
    }

    private void UpdateClocks()
    {
        var now = DateTimeOffset.UtcNow;
        var brazil = TimeZoneInfo.ConvertTime(now, Brazil).ToString("HH:mm", CultureInfo.InvariantCulture);
        var moscow = TimeZoneInfo.ConvertTime(now, Moscow).ToString("HH:mm", CultureInfo.InvariantCulture);
        Clocks.Text = $"SÃO PAULO {brazil}   ·   МОСКВА {moscow}";
    }

    private void OnWindowStateChanged()
    {
        // Sem a borda padrão, a janela maximizada passa da tela pelo tamanho da borda de redimensionamento.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static TimeZoneInfo FindZone(string ianaId, string windowsId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }
}
