namespace DinoTranslator.Shared.Auth;

public static class AuthRoutes
{
    public const string Login = "/api/auth/login";
    public const string Refresh = "/api/auth/refresh";
    public const string Logout = "/api/auth/logout";
    public const string Me = "/api/me";
}

public sealed record LoginRequest(string UserName, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record UserProfile(Guid Id, string UserName, string DisplayName, string Language, Gender Gender);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserProfile User);
