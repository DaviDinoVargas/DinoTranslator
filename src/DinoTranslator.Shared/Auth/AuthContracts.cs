using System.Text.Json.Serialization;

namespace DinoTranslator.Shared.Auth;

public static class AuthRoutes
{
    public const string Login = "/api/auth/login";
    public const string Register = "/api/auth/register";
    public const string Refresh = "/api/auth/refresh";
    public const string Logout = "/api/auth/logout";
    public const string Me = "/api/me";
}

public sealed record LoginRequest(string UserName, string Password);

/// <summary>Cadastro pela rede (se o servidor permitir). Code: código de cadastro, se o servidor exigir.</summary>
public sealed record RegisterRequest(string UserName, string Password, string DisplayName, string Language, Gender Gender, string? Code = null);

/// <summary>
/// Grátis: entra e cuida do perfil. Plus: grupos, chat, chamadas em grupo e os adicionais. Pro:
/// tudo do Plus e a camada de IA (com créditos). Ver a matriz de planos.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccountPlan>))]
public enum AccountPlan
{
    Free = 0,
    Plus = 1,
    Pro = 2,
}

public sealed record RefreshRequest(string RefreshToken);

public sealed record UserProfile(
    Guid Id,
    string UserName,
    string DisplayName,
    string Language,
    Gender Gender,
    AccountPlan Plan = AccountPlan.Free,
    DateTimeOffset? PlanExpiresAt = null);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserProfile User);
