using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DinoTranslator.Server.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DinoTranslator.Server.Auth;

public sealed class TokenService(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user)
    {
        var now = time.GetUtcNow();
        var expiresAt = now.AddMinutes(auth.Value.AccessTokenMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer,
            Audience = jwt.Value.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = Claims(user, now.UtcDateTime),
            SigningCredentials = new SigningCredentials(jwt.Value.CreateSecurityKey(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expiresAt);
    }

    private static Dictionary<string, object> Claims(User user, DateTime nowUtc)
    {
        var claims = new Dictionary<string, object>
        {
            [AuthClaims.Subject] = user.Id.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            [AuthClaims.Name] = user.UserName,
            [AuthClaims.SecurityStamp] = user.SecurityStamp.ToString(),
        };
        if (user.HasPlus(nowUtc))
        {
            claims[AuthClaims.Plan] = "plus";
            if (user.PlanExpiresAtUtc is { } until)
            {
                claims[AuthClaims.PlanUntil] = new DateTimeOffset(until, TimeSpan.Zero).ToUnixTimeSeconds();
            }
        }
        return claims;
    }

    /// <summary>256 bits aleatórios; o servidor guarda só o SHA-256.</summary>
    public static string NewRefreshToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] HashRefreshToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
