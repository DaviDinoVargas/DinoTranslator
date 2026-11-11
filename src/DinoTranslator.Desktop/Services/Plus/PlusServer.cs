using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DinoTranslator.Desktop.Services.Plus;

/// <summary>
/// Endereço do servidor da versão paga (o seu servidor), escolhido no app e guardado nos dados
/// locais. Só HTTPS; em desenvolvimento, também http://localhost.
/// </summary>
public sealed class PlusServerStore(string path)
{
    private Uri? _current = Load(path);

    public event Action? Changed;

    public Uri? Current => _current;

    /// <summary>Confere e guarda; devolve a mensagem de erro, se o endereço não servir.</summary>
    public string? Save(string? value)
    {
        if (Parse(value) is not { } uri)
        {
            return "Use o endereço do servidor com https://, só com o domínio (e a porta, se houver).";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { servidor = uri.AbsoluteUri }));
        _current = uri;
        Changed?.Invoke();
        return null;
    }

    public static Uri? Parse(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length > 0 && !text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.AbsolutePath is not ("/" or ""))
        {
            return null;
        }
        var allowed = uri.Scheme == Uri.UriSchemeHttps;
#if DEBUG
        allowed |= uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
#endif
        return allowed ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/") : null;
    }

    private static Uri? Load(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("servidor", out var value) ? Parse(value.GetString()) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Cliente HTTP do servidor atual: trocar o endereço troca o cliente.</summary>
public sealed class PlusHttp : IDisposable
{
    private readonly PlusServerStore _server;
    private HttpClient? _client;
    private Uri? _clientUri;

    public PlusHttp(PlusServerStore server) => _server = server;

    public HttpClient Client
    {
        get
        {
            var uri = _server.Current ?? throw new InvalidOperationException("Servidor da versão paga não configurado.");
            if (_client is null || _clientUri != uri)
            {
                _client?.Dispose();
                _client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };
                _client.DefaultRequestHeaders.UserAgent.ParseAdd("DinoTranslator");
                _clientUri = uri;
            }
            return _client;
        }
    }

    public void Dispose() => _client?.Dispose();
}
