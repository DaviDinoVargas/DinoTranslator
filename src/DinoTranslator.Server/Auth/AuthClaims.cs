using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DinoTranslator.Server.Auth;

public static class AuthClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string Name = "name";
    public const string SecurityStamp = "stamp";

    /// <summary>"plus" ou "pro" quando o plano pago está valendo na emissão do token.</summary>
    public const string Plan = "plano";

    /// <summary>Fim do Plus (segundos Unix), se tiver prazo.</summary>
    public const string PlanUntil = "plano_ate";

    /// <summary>Versão paga: Plus ou Pro.</summary>
    public const string PlusPolicy = "plus";

    /// <summary>Camada de IA: só o Pro.</summary>
    public const string ProPolicy = "pro";

    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(Subject), out var id) ? id : null;
}
