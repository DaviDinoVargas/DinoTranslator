using System.IO;
using System.Text.Json;

namespace DinoTranslator.Desktop.Services;

public enum ConnectionMode
{
    /// <summary>Padrão: sem servidor, a chamada é aberta por códigos de convite e de resposta.</summary>
    Code,

    /// <summary>Opcional: login e presença pelo DinoTranslator.Server.</summary>
    Server,
}

/// <summary>
/// Configuração do app (em <c>appsettings.json</c>, ao lado do executável) e pastas de
/// dados do usuário em %LOCALAPPDATA%\DinoTranslator.
/// </summary>
/// <param name="RendezvousUri">Serviço de mensagens do encontro automático; <c>null</c> desliga.</param>
public sealed record DesktopSettings(ConnectionMode Mode, Uri? ServerUri, string DataFolder, Uri? RendezvousUri = null)
{
    public const string FileName = "appsettings.json";

    /// <summary>Serviço público usado se o appsettings.json não disser outro.</summary>
    public static readonly Uri DefaultRendezvous = new("https://ntfy.sh/");

    public string WebViewDataFolder => Path.Combine(DataFolder, "WebView2");

    public string SessionFile => Path.Combine(DataFolder, "session.dat");

    public string ProfileFile => Path.Combine(DataFolder, "perfil.json");

    public string TranslationFile => Path.Combine(DataFolder, "motores.dat");

    public string ModelsFolder => Path.Combine(DataFolder, "modelos");

    public static DesktopSettings Load()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinoTranslator");
        var (mode, serverUrl, rendezvousUrl) = ReadFile(Path.Combine(AppContext.BaseDirectory, FileName));
#if DEBUG
        // Só em desenvolvimento: duas instâncias com perfis separados no mesmo computador.
        dataFolder = Environment.GetEnvironmentVariable("DINOTRANSLATOR_DATA") ?? dataFolder;
        mode = Environment.GetEnvironmentVariable("DINOTRANSLATOR_MODE") ?? mode;
        serverUrl = Environment.GetEnvironmentVariable("DINOTRANSLATOR_SERVER") ?? serverUrl;
        rendezvousUrl = Environment.GetEnvironmentVariable("DINOTRANSLATOR_RENDEZVOUS") ?? rendezvousUrl;
#endif
        var connectionMode = ParseMode(mode);
        var serverUri = connectionMode == ConnectionMode.Server ? ParseServerUri(serverUrl) : null;
        return new DesktopSettings(connectionMode, serverUri, dataFolder, ParseRendezvousUri(rendezvousUrl));
    }

    /// <summary>Ausente: o serviço padrão; vazio ou "desligado": só os códigos; senão, HTTPS.</summary>
    public static Uri? ParseRendezvousUri(string? value)
    {
        if (value is null)
        {
            return DefaultRendezvous;
        }
        if (value.Length == 0 || value == "desligado")
        {
            return null;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException("O serviço do encontro automático precisa ser um endereço HTTPS.");
        }
        return uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    public static ConnectionMode ParseMode(string? value) => value switch
    {
        null or "" or "codigo" => ConnectionMode.Code,
        "servidor" => ConnectionMode.Server,
        _ => throw new InvalidOperationException("Modo de conexão inválido: use \"codigo\" ou \"servidor\"."),
    };

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

    private static (string? Mode, string? ServerUrl, string? RendezvousUrl) ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return (null, null, null);
        }
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var mode = root.TryGetProperty("Conexao", out var connection) && connection.TryGetProperty("Modo", out var modeValue)
            ? modeValue.GetString()
            : null;
        var serverUrl = root.TryGetProperty("Server", out var server) && server.TryGetProperty("BaseUrl", out var baseUrl)
            ? baseUrl.GetString()
            : null;
        var rendezvousUrl = root.TryGetProperty("Encontro", out var rendezvous) && rendezvous.TryGetProperty("Servidor", out var rendezvousValue)
            ? rendezvousValue.GetString()
            : null;
        return (mode, serverUrl, rendezvousUrl);
    }
}
