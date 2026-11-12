using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Room;

namespace DinoTranslator.Desktop.Services.Plus;

/// <summary>Servidor ICE no formato do RTCIceServer do WebRTC (os nomes viram urls/username/credential).</summary>
public sealed record RtcIceServer(IReadOnlyList<string> Urls, string? Username, string? Credential);

/// <summary>Retransmissão (TURN) para a chamada por código, quando a pessoa tem uma.</summary>
public interface IRelayProvider
{
    /// <summary>Os servidores da retransmissão, ou null (sem conta Plus, sem servidor ou fora do ar).</summary>
    Task<IReadOnlyList<RtcIceServer>?> RelayAsync(CancellationToken cancellationToken);
}

/// <summary>
/// "Conexão garantida" do Plus: com a conta Plus ou Pro aberta neste computador, a chamada por
/// código usa a retransmissão do seu servidor quando a conexão direta não fecha (CGNAT, firewall).
/// A credencial é temporária (o servidor emite, o coturn confere a validade sozinho) e fica em
/// cache até perto de vencer. Qualquer falha devolve null: a chamada segue só com a conexão direta.
/// </summary>
public sealed class PlusRelay(PlusHttp plus, AuthClient auth, PlusServerStore server, TimeProvider time) : IRelayProvider
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(30);

    /// <summary>Credencial guardada por conta e servidor: sair da conta ou trocar de servidor não a reaproveita.</summary>
    private sealed record Cached(Guid User, Uri Server, IReadOnlyList<RtcIceServer> Relay, DateTimeOffset Until);

    private volatile Cached? _cache;

    public async Task<IReadOnlyList<RtcIceServer>?> RelayAsync(CancellationToken cancellationToken)
    {
        if (server.Current is not { } current)
        {
            return null;
        }
        try
        {
            // "Manter conectado" vale aqui também: a conexão garantida não exige abrir a tela do Plus antes.
            if (auth.CurrentUser is null && !await auth.TryResumeAsync(cancellationToken))
            {
                return null;
            }
            if (auth.CurrentUser is not { Plan: AccountPlan.Plus or AccountPlan.Pro } user)
            {
                return null;
            }
            if (_cache is { } cache && cache.User == user.Id && cache.Server == current && time.GetUtcNow() < cache.Until)
            {
                return cache.Relay;
            }
            if (await auth.GetAccessTokenAsync(cancellationToken) is not { } token)
            {
                return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, RoomRoutes.IceServers);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await plus.Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode || await response.Content.ReadFromJsonAsync<IceServersResponse>(cancellationToken) is not { } servers)
            {
                return null;
            }
            var relay = ToRtc(servers);
            if (relay.Count == 0)
            {
                return null;
            }
            _cache = new Cached(user.Id, current, relay, servers.ExpiresAt - RenewBefore);
            return relay;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Só TURN (o STUN público o app já tem); endereços conferidos.</summary>
    public static IReadOnlyList<RtcIceServer> ToRtc(IceServersResponse response) =>
        [.. response.IceServers
            .Where(s => s.Username is { Length: > 0 and <= 256 } && s.Credential is { Length: > 0 and <= 256 })
            .Select(s => new RtcIceServer([.. s.Urls.Where(IsTurn).Take(4)], s.Username, s.Credential))
            .Where(s => s.Urls.Count > 0)];

    private static bool IsTurn(string url) =>
        url.Length <= 256 && (url.StartsWith("turn:", StringComparison.Ordinal) || url.StartsWith("turns:", StringComparison.Ordinal));
}
