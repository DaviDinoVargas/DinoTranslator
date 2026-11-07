using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DinoTranslator.Desktop.Services;

/// <summary>
/// Cliente do serviço de mensagens do encontro automático (protocolo do ntfy: publicar com
/// POST, acompanhar com GET …/json). Só transporta texto já cifrado por
/// <see cref="DinoTranslator.Shared.Call.CallRendezvous"/>; tudo o que chega é tratado como hostil.
/// </summary>
public sealed class RendezvousRelay(HttpClient http, Uri? server)
{
    private const int MaxLineLength = 16 * 1024;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(20);

    /// <summary>Desligado quando o appsettings.json deixa o servidor do encontro vazio.</summary>
    public bool Enabled => server is not null;

    /// <summary>Publica uma mensagem; devolve <c>false</c> se o serviço não respondeu.</summary>
    public async Task<bool> PublishAsync(string topic, string message, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return false;
        }
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, topic))
                {
                    Content = new StringContent(message, Encoding.ASCII, "text/plain"),
                };
                // Sem repasse para notificações de celular: a mensagem só serve para o outro app.
                request.Headers.TryAddWithoutValidation("Firebase", "no");
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Rede instável: tenta de novo.
            }
            if (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
        return false;
    }

    /// <summary>
    /// Acompanha o tópico até ser cancelado, reconectando se a conexão cair; cada mensagem nova
    /// (inclusive as publicadas desde <paramref name="since"/>) é entregue uma vez.
    /// </summary>
    public async Task ListenAsync(string topic, DateTimeOffset since, Func<string, Task> onMessage, CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var retry = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var uri = new Uri(server, $"{topic}/json?since={since.ToUnixTimeSeconds()}");
                using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                retry = TimeSpan.FromSeconds(1);
                while (await ReadLineAsync(reader, cancellationToken) is { } line)
                {
                    if (Parse(line, topic) is { } entry && seen.Add(entry.Id))
                    {
                        await onMessage(entry.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // Conexão caiu: volta a acompanhar depois de uma pausa crescente.
            }
            await Task.Delay(retry, cancellationToken);
            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    /// <summary>Linha do fluxo JSON → (id, texto) se for uma mensagem do tópico esperado.</summary>
    internal static (string Id, string Message)? Parse(string line, string topic)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("event", out var kind) && kind.ValueEquals("message")
                && root.TryGetProperty("topic", out var name) && name.ValueEquals(topic)
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return (id.GetString()!, message.GetString()!);
            }
        }
        catch (JsonException)
        {
            // Linha que não é JSON: ignorada.
        }
        return null;
    }

    /// <summary>Lê uma linha de no máximo <see cref="MaxLineLength"/> caracteres; o excesso é descartado.</summary>
    internal static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var tooLong = false;
        var buffer = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) == 0)
            {
                return line.Length > 0 && !tooLong ? line.ToString() : null;
            }
            var c = buffer[0];
            if (c == '\n')
            {
                if (!tooLong)
                {
                    return line.ToString();
                }
                line.Clear();
                tooLong = false;
                continue;
            }
            if (line.Length < MaxLineLength)
            {
                line.Append(c);
            }
            else
            {
                tooLong = true;
            }
        }
    }
}
