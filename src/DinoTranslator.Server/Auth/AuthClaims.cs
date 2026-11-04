using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DinoTranslator.Server.Auth;

public static class AuthClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string Name = "name";
    public const string SecurityStamp = "stamp";

    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(Subject), out var id) ? id : null;
}
