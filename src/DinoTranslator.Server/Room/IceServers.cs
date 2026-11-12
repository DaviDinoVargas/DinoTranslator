using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DinoTranslator.Server.Auth;
using DinoTranslator.Shared.Room;
using Microsoft.Extensions.Options;

namespace DinoTranslator.Server.Room;

public sealed class IceOptions
{
    public const string Section = "Ice";

    /// <summary>Ex.: <c>stun:turn.exemplo.com:3478</c>.</summary>
    public string[] StunUrls { get; set; } = [];

    /// <summary>Ex.: <c>turn:turn.exemplo.com:3478?transport=udp</c>, <c>turns:turn.exemplo.com:5349?transport=tcp</c>.</summary>
    public string[] TurnUrls { get; set; } = [];

    /// <summary>Mesmo valor do <c>static-auth-secret</c> do coturn. Nunca no repositório.</summary>
    public string TurnSecret { get; set; } = string.Empty;

    public int CredentialHours { get; set; } = 12;
}

public static class IceServers
{
    public static IServiceCollection AddIceServers(this IServiceCollection services)
    {
        services.AddOptions<IceOptions>()
            .BindConfiguration(IceOptions.Section)
            .Validate(o => o.CredentialHours is >= 1 and <= 24, "Ice:CredentialHours deve ficar entre 1 e 24.")
            .Validate(o => o.TurnUrls.Length == 0 || o.TurnSecret.Length >= 32, "Ice:TurnSecret precisa de pelo menos 32 caracteres.")
            .ValidateOnStart();
        return services;
    }

    public static IEndpointRouteBuilder MapIceServers(this IEndpointRouteBuilder app)
    {
        app.MapGet(RoomRoutes.IceServers, (ClaimsPrincipal principal, IOptions<IceOptions> options, TimeProvider time) =>
            principal.GetUserId() is { } userId
                ? Results.Ok(Create(options.Value, userId, time.GetUtcNow()))
                : Results.Unauthorized())
            // Conexão garantida é do plano pago: a banda da retransmissão sai do seu servidor.
            .RequireAuthorization(AuthClaims.PlusPolicy);
        return app;
    }

    /// <summary>
    /// Credencial temporária no formato do <c>use-auth-secret</c> do coturn:
    /// usuário = "&lt;expiração unix&gt;:&lt;apelido&gt;", senha = base64(HMAC-SHA1(segredo, usuário)).
    /// O coturn recusa a credencial depois da expiração, sem precisar consultar o servidor.
    /// O apelido é derivado da conta com o segredo: estável para a mesma pessoa, mas o id da conta
    /// não aparece na rede (o usuário do TURN não é cifrado) nem nos registros do coturn.
    /// </summary>
    public static IceServersResponse Create(IceOptions options, Guid userId, DateTimeOffset now)
    {
        var expiresAt = now.AddHours(options.CredentialHours);
        var servers = new List<IceServer>();

        if (options.StunUrls.Length > 0)
        {
            servers.Add(new IceServer(options.StunUrls, null, null));
        }

        if (options.TurnUrls.Length > 0 && options.TurnSecret.Length > 0)
        {
            var username = $"{expiresAt.ToUnixTimeSeconds()}:{Alias(options.TurnSecret, userId)}";
            // HMAC-SHA1 é o que o protocolo do coturn exige; como MAC ele continua seguro.
            var credential = Convert.ToBase64String(
                HMACSHA1.HashData(Encoding.UTF8.GetBytes(options.TurnSecret), Encoding.UTF8.GetBytes(username)));
            servers.Add(new IceServer(options.TurnUrls, username, credential));
        }

        return new IceServersResponse(servers, expiresAt);
    }

    /// <summary>128 bits do HMAC-SHA256 da conta com o segredo do TURN, em hexadecimal.</summary>
    public static string Alias(string secret, Guid userId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"dino-turn:{userId:N}")), 0, 16);
}
