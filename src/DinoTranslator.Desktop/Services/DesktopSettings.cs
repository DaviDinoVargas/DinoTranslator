using System.IO;
using System.Text.Json;

namespace DinoTranslator.Desktop.Services;

/// <summary>
/// Configuração do app: endereço do servidor (em <c>appsettings.json</c>, ao lado do executável)
/// e pastas de dados do usuário em %LOCALAPPDATA%\DinoTranslator.
/// </summary>
public sealed record DesktopSettings(Uri ServerUri, string DataFolder)
{
    public const string FileName = "appsettings.json";

    public string WebViewDataFolder => Path.Combine(DataFolder, "WebView2");

    public string SessionFile => Path.Combine(DataFolder, "session.dat");

    public static DesktopSettings Load()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinoTranslator");
        var serverUrl = ReadServerUrl(Path.Combine(AppContext.BaseDirectory, FileName));
#if DEBUG
        serverUrl = Environment.GetEnvironmentVariable("DINOTRANSLATOR_SERVER") ?? serverUrl;
#endif
        return new DesktopSettings(ParseServerUri(serverUrl), dataFolder);
    }

    /// <summary>Só HTTPS; HTTP apenas para o próprio computador, em desenvolvimento.</summary>
    public static Uri ParseServerUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("O endereço do servidor não está configurado.");
        }
        var allowedScheme = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!allowedScheme)
        {
            throw new InvalidOperationException("O servidor precisa usar HTTPS.");
        }
        if (uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException("O endereço do servidor deve ter só esquema, domínio e porta.");
        }
        return uri;
    }

    private static string? ReadServerUrl(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("Server", out var server)
            && server.TryGetProperty("BaseUrl", out var baseUrl)
                ? baseUrl.GetString()
                : null;
    }
}
