using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using DinoTranslator.Desktop.Components;
using DinoTranslator.Desktop.Services;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly TitleBarState _titleBar;

    public MainWindow(IServiceProvider services, DesktopSettings settings)
    {
        InitializeComponent();
        _titleBar = services.GetRequiredService<TitleBarState>();

        var webView = new BlazorWebView { HostPage = @"wwwroot\index.html", Services = services };
        webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        WebViewSecurity.Apply(webView, settings);
        Host.Children.Add(webView);

        UpdateClocks();
        _clockTimer.Tick += (_, _) => UpdateClocks();
        _titleBar.Changed += () => Dispatcher.BeginInvoke(UpdateClocks);
        _clockTimer.Start();
        StateChanged += (_, _) => OnWindowStateChanged();
        Closed += (_, _) => _clockTimer.Stop();
    }

    /// <summary>Seu horário e, durante a chamada, o horário de quem está do outro lado.</summary>
    private void UpdateClocks()
    {
        var now = DateTimeOffset.UtcNow;
        var text = $"VOCÊ {FormatTime(now, _titleBar.LocalTimeZone)}";
        if (_titleBar.PeerName is { } peerName && _titleBar.PeerTimeZone is { } peerZone)
        {
            text += $"   ·   {peerName.ToUpperInvariant()} {FormatTime(now, peerZone)}";
        }
        Clocks.Text = text;
        Status.Text = _titleBar.Status ?? string.Empty;
    }

    private static string FormatTime(DateTimeOffset now, string timeZoneId) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone)
            ? TimeZoneInfo.ConvertTime(now, zone).ToString("HH:mm", CultureInfo.InvariantCulture)
            : "--:--";

    private void OnWindowStateChanged()
    {
        // Sem a borda padrão, a janela maximizada passa da tela pelo tamanho da borda de redimensionamento.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
