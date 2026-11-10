using System.Security.Claims;
using DinoTranslator.Shared.Auth;

namespace DinoTranslator.Server.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(AuthRoutes.Login, async (LoginRequest request, AuthService auth, CancellationToken cancellationToken) =>
            await auth.LoginAsync(request.UserName, request.Password, cancellationToken) is { } tokens
                ? Results.Ok(tokens)
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Usuário ou senha inválidos."))
            .RequireRateLimiting(AuthSetup.LoginRateLimit);

        app.MapPost(AuthRoutes.Register, async (RegisterRequest request, AuthService auth, CancellationToken cancellationToken) =>
        {
            var result = await auth.RegisterAsync(request, cancellationToken);
            return result.Tokens is { } tokens
                ? Results.Ok(tokens)
                : Results.Problem(statusCode: result.Closed ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest, title: result.Error);
        })
            .RequireRateLimiting(AuthSetup.RegisterRateLimit);

        app.MapPost(AuthRoutes.Refresh, async (RefreshRequest request, AuthService auth, CancellationToken cancellationToken) =>
            await auth.RefreshAsync(request.RefreshToken, cancellationToken) is { } tokens
                ? Results.Ok(tokens)
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sessão expirada. Entre novamente."))
            .RequireRateLimiting(AuthSetup.RefreshRateLimit);

        app.MapPost(AuthRoutes.Logout, async (RefreshRequest request, AuthService auth, CancellationToken cancellationToken) =>
        {
            await auth.LogoutAsync(request.RefreshToken, cancellationToken);
            return Results.NoContent();
        })
            .RequireRateLimiting(AuthSetup.RefreshRateLimit);

        app.MapGet(AuthRoutes.Me, async (ClaimsPrincipal principal, AuthService auth, CancellationToken cancellationToken) =>
            principal.GetUserId() is { } userId && await auth.GetProfileAsync(userId, cancellationToken) is { } profile
                ? Results.Ok(profile)
                : Results.Unauthorized())
            .RequireAuthorization();

        return app;
    }
}
