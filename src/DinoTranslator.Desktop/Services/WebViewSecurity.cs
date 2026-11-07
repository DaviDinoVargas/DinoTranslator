using System.Diagnostics;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Web.WebView2.Core;

namespace DinoTranslator.Desktop.Services;

/// <summary>
/// Restringe o WebView2 ao conteúdo empacotado no app: sem navegação para fora, sem novas
/// janelas, sem downloads e com câmera e microfone liberados só para a própria interface.
/// </summary>
internal static class WebViewSecurity
{
    // Endereços em que o BlazorWebView serve o conteúdo local do app.
    private static readonly string[] AppHosts = ["0.0.0.0", "0.0.0.1"];

#if DEBUG
    private const bool DeveloperMode = true;
#else
    private const bool DeveloperMode = false;
#endif

    /// <summary>
    /// Variáveis de ambiente que o WebView2 lê ao iniciar e que permitiriam a um atalho ou
    /// lançador malicioso ligar a depuração remota, trocar o navegador ou a pasta de dados.
    /// </summary>
    private static readonly string[] EnvironmentOverrides =
    [
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
        "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
        "WEBVIEW2_USER_DATA_FOLDER",
        "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE",
        "WEBVIEW2_CHANNEL_SEARCH_KIND",
    ];

    /// <summary>Na versão final, o WebView2 sobe só com a configuração do próprio app.</summary>
    public static void ClearEnvironmentOverrides()
    {
#if !DEBUG
        foreach (var variable in EnvironmentOverrides)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
#endif
    }

    public static void Apply(BlazorWebView webView, DesktopSettings settings)
    {
        // Fora da pasta do programa: em "Arquivos de Programas" o usuário não tem permissão de escrita.
        webView.BlazorWebViewInitializing += (_, e) => e.UserDataFolder = settings.WebViewDataFolder;
        webView.BlazorWebViewInitialized += (_, e) => Harden(e.WebView.CoreWebView2);
        webView.UrlLoading += (_, e) =>
        {
            if (IsAppOrigin(e.Url))
            {
                e.UrlLoadingStrategy = UrlLoadingStrategy.OpenInWebView;
                return;
            }
            e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
            OfferToOpenInBrowser(e.Url);
        };
    }

    internal static bool IsAppOrigin(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && AppHosts.Contains(uri.Host);

    internal static bool IsAppOrigin(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsAppOrigin(parsed);

    private static void Harden(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = DeveloperMode;
        settings.AreDefaultContextMenusEnabled = DeveloperMode;
        settings.AreBrowserAcceleratorKeysEnabled = DeveloperMode;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!IsAppOrigin(e.Uri))
            {
                e.Cancel = true;
            }
        };
        core.PermissionRequested += (_, e) =>
        {
            var allowed = IsAppOrigin(e.Uri)
                && e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone;
            e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };
    }

    private static void OfferToOpenInBrowser(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }
        var answer = MessageBox.Show(
            Application.Current.MainWindow,
            $"Abrir este link no navegador?\n\n{url.AbsoluteUri}",
            "DinoTranslator",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
