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
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Usuário ou senha inválidos."));

        app.MapPost(AuthRoutes.Refresh, async (RefreshRequest request, AuthService auth, CancellationToken cancellationToken) =>
            await auth.RefreshAsync(request.RefreshToken, cancellationToken) is { } tokens
                ? Results.Ok(tokens)
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sessão expirada. Entre novamente."));

        app.MapPost(AuthRoutes.Logout, async (RefreshRequest request, AuthService auth, CancellationToken cancellationToken) =>
        {
            await auth.LogoutAsync(request.RefreshToken, cancellationToken);
            return Results.NoContent();
        });

        app.MapGet(AuthRoutes.Me, async (ClaimsPrincipal principal, AuthService auth, CancellationToken cancellationToken) =>
            principal.GetUserId() is { } userId && await auth.GetProfileAsync(userId, cancellationToken) is { } profile
                ? Results.Ok(profile)
                : Results.Unauthorized())
            .RequireAuthorization();

        return app;
    }
}
