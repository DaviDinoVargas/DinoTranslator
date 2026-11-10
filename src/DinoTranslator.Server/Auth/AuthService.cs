using DinoTranslator.Server.Data;
using DinoTranslator.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DinoTranslator.Server.Auth;

/// <summary>Resultado do cadastro: os tokens (já entra) ou o motivo da recusa.</summary>
public sealed record RegistrationResult(TokenResponse? Tokens, string? Error, bool Closed = false);

public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    DummyPasswordHash dummy,
    TokenService tokens,
    UserAdministration accounts,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<AuthService> logger)
{
    private const int MaxRefreshTokenLength = 128;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Devolve <c>null</c> para qualquer falha — o motivo nunca é revelado ao cliente.</summary>
    public async Task<TokenResponse?> LoginAsync(string? userName, string? password, CancellationToken cancellationToken)
    {
        password ??= string.Empty;
        if (password.Length > Credentials.MaxPasswordLength)
        {
            return null;
        }

        var normalized = Credentials.NormalizeUserName(userName);
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserName == normalized, cancellationToken);
        var now = Now;

        if (user is null)
        {
            dummy.Verify(password);
            return null;
        }

        if (user.LockoutEndUtc > now)
        {
            dummy.Verify(password);
            logger.LogWarning("Tentativa de login na conta bloqueada {UserId}.", user.Id);
            return null;
        }

        if (user.DisabledAtUtc is not null)
        {
            dummy.Verify(password);
            logger.LogWarning("Tentativa de login na conta suspensa {UserId}.", user.Id);
            return null;
        }

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            RegisterFailure(user, now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
        }
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;

        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.ExpiresAtUtc < now)
            .ExecuteDeleteAsync(cancellationToken);

        var response = IssueTokens(user, sessionId: Guid.NewGuid(), now);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Login da conta {UserId}.", user.Id);
        return response;
    }

    /// <summary>
    /// Troca o refresh token por um novo da mesma sessão. Um token já trocado sendo
    /// apresentado de novo indica roubo: a sessão inteira é revogada.
    /// </summary>
    public async Task<TokenResponse?> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > MaxRefreshTokenLength)
        {
            return null;
        }

        var hash = TokenService.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null || stored.RevokedAtUtc is not null)
        {
            return null;
        }

        var now = Now;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.UsedAtUtc == null && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAtUtc, now), cancellationToken);
        if (claimed == 0)
        {
            await RevokeSessionAsync(stored.SessionId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogWarning("Refresh token reutilizado na sessão {SessionId}; sessão revogada.", stored.SessionId);
            return null;
        }

        if (stored.ExpiresAtUtc <= now)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (stored.User.DisabledAtUtc is not null)
        {
            await RevokeSessionAsync(stored.SessionId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var response = IssueTokens(stored.User, stored.SessionId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    /// <summary>
    /// Cadastro pela rede, se o servidor permitir (e com o código, se ele exigir). A conta nasce
    /// no plano grátis; o Plus é ativado pelo dono do servidor.
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(RegisterRequest? request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.OpenRegistration)
        {
            return new RegistrationResult(null, "O cadastro está fechado neste servidor.", Closed: true);
        }
        if (request is null)
        {
            return new RegistrationResult(null, "Cadastro inválido.");
        }
        if (!string.IsNullOrEmpty(settings.RegistrationCode) && !SameSecret(request.Code, settings.RegistrationCode))
        {
            return new RegistrationResult(null, "Código de cadastro inválido.");
        }

        User user;
        try
        {
            user = await accounts.CreateAsync(
                new NewUser(request.UserName, request.DisplayName, request.Language, request.Gender, request.Password ?? string.Empty),
                cancellationToken);
        }
        catch (UserAdministrationException ex)
        {
            return new RegistrationResult(null, ex.Message);
        }

        var response = IssueTokens(user, sessionId: Guid.NewGuid(), Now);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Conta {UserId} criada pelo cadastro.", user.Id);
        return new RegistrationResult(response, null);
    }

    private static bool SameSecret(string? given, string expected)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(given ?? string.Empty);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > MaxRefreshTokenLength)
        {
            return;
        }

        var hash = TokenService.HashRefreshToken(refreshToken);
        var sessionId = await db.RefreshTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.SessionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (sessionId is { } id)
        {
            await RevokeSessionAsync(id, Now, cancellationToken);
        }
    }

    public async Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : ToProfile(user);
    }

    public static UserProfile ToProfile(User user) =>
        new(user.Id, user.UserName, user.DisplayName, user.Language, user.Gender, user.Plan,
            user.PlanExpiresAtUtc is { } until ? new DateTimeOffset(until, TimeSpan.Zero) : null);

    private void RegisterFailure(User user, DateTime now)
    {
        user.FailedLoginCount++;
        if (user.FailedLoginCount < options.Value.MaxFailedAttempts)
        {
            return;
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = now.AddMinutes(options.Value.LockoutMinutes);
        logger.LogWarning("Conta {UserId} bloqueada por {Minutes} min após tentativas erradas.", user.Id, options.Value.LockoutMinutes);
    }

    private TokenResponse IssueTokens(User user, Guid sessionId, DateTime now)
    {
        var refreshToken = TokenService.NewRefreshToken();
        var refreshExpiresAt = now.AddDays(options.Value.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            SessionId = sessionId,
            TokenHash = TokenService.HashRefreshToken(refreshToken),
            CreatedAtUtc = now,
            ExpiresAtUtc = refreshExpiresAt,
        });

        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user);
        return new TokenResponse(
            accessToken,
            accessExpiresAt,
            refreshToken,
            new DateTimeOffset(refreshExpiresAt, TimeSpan.Zero),
            ToProfile(user));
    }

    private Task<int> RevokeSessionAsync(Guid sessionId, DateTime now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.SessionId == sessionId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAtUtc, now), cancellationToken);
}
