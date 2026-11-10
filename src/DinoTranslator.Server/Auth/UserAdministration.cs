using DinoTranslator.Server.Data;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DinoTranslator.Server.Auth;

public sealed record NewUser(string UserName, string DisplayName, string Language, Gender Gender, string Password);

public sealed class UserAdministrationException(string message) : Exception(message);

/// <summary>
/// Criação e manutenção das contas: pelo cadastro (se aberto) e pelo comando <c>users</c> no
/// próprio servidor, que também ativa o plano e suspende contas.
/// </summary>
public sealed class UserAdministration(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<AuthOptions> options,
    SessionValidator sessions,
    TimeProvider time)
{
    public async Task<User> CreateAsync(NewUser request, CancellationToken cancellationToken = default)
    {
        var userName = Credentials.NormalizeUserName(request.UserName);
        if (!Credentials.IsValidUserName(userName))
        {
            throw new UserAdministrationException("Usuário inválido: use de 3 a 32 caracteres entre a-z, 0-9, ponto, hífen e sublinhado.");
        }

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length is 0 or > Credentials.MaxDisplayNameLength)
        {
            throw new UserAdministrationException($"O nome de exibição precisa ter de 1 a {Credentials.MaxDisplayNameLength} caracteres.");
        }

        if (!Languages.IsSupported(request.Language))
        {
            throw new UserAdministrationException($"Idioma inválido: use {string.Join(", ", Languages.All.Select(l => l.Code))}.");
        }

        if (!Enum.IsDefined(request.Gender))
        {
            throw new UserAdministrationException("Gênero inválido.");
        }

        if (Credentials.ValidatePassword(request.Password, userName) is { } passwordError)
        {
            throw new UserAdministrationException(passwordError);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var maxUsers = options.Value.MaxUsers;
        if (maxUsers > 0 && await db.Users.CountAsync(cancellationToken) >= maxUsers)
        {
            throw new UserAdministrationException($"Limite de {maxUsers} contas atingido.");
        }

        if (await db.Users.AnyAsync(u => u.UserName == userName, cancellationToken))
        {
            throw new UserAdministrationException($"O usuário \"{userName}\" já existe.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            DisplayName = displayName,
            Language = request.Language,
            Gender = request.Gender,
            PasswordHash = string.Empty,
            SecurityStamp = Guid.NewGuid(),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Dois cadastros com o mesmo nome ao mesmo tempo: o índice único decide.
            throw new UserAdministrationException($"O usuário \"{userName}\" já existe.");
        }
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    /// <summary>Ativa o Plus (por <paramref name="days"/> dias, ou sem prazo) ou volta ao grátis.</summary>
    public async Task<User> SetPlanAsync(string userName, AccountPlan plan, int? days, CancellationToken cancellationToken = default)
    {
        if (days is <= 0 or > 3660)
        {
            throw new UserAdministrationException("Prazo inválido: use de 1 a 3660 dias.");
        }
        var user = await FindAsync(userName, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        user.Plan = plan;
        // Renovação soma ao prazo que ainda resta.
        var start = plan == AccountPlan.Plus && user.PlanExpiresAtUtc > now ? user.PlanExpiresAtUtc.Value : now;
        user.PlanExpiresAtUtc = plan == AccountPlan.Plus && days is { } d ? start.AddDays(d) : null;
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>Suspende a conta: encerra todas as sessões e recusa novas entradas.</summary>
    public async Task DisableAsync(string userName, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userName, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        user.DisabledAtUtc = now;
        user.SecurityStamp = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAtUtc, now), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await sessions.InvalidateAsync(user.Id, cancellationToken);
    }

    public async Task EnableAsync(string userName, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userName, cancellationToken);
        user.DisabledAtUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        await sessions.InvalidateAsync(user.Id, cancellationToken);
    }

    public Task<List<User>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().OrderBy(u => u.CreatedAtUtc).ToListAsync(cancellationToken);

    /// <summary>Troca a senha, desbloqueia a conta e encerra todas as sessões abertas.</summary>
    public async Task ResetPasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userName, cancellationToken);
        if (Credentials.ValidatePassword(newPassword, user.UserName) is { } passwordError)
        {
            throw new UserAdministrationException(passwordError);
        }

        user.PasswordHash = hasher.HashPassword(user, newPassword);
        user.SecurityStamp = Guid.NewGuid();
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAtUtc, now), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await sessions.InvalidateAsync(user.Id, cancellationToken);
    }

    public async Task UnlockAsync(string userName, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userName, cancellationToken);
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(string userName, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userName, cancellationToken);
        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        await sessions.InvalidateAsync(user.Id, cancellationToken);
    }

    private async Task<User> FindAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = Credentials.NormalizeUserName(userName);
        return await db.Users.SingleOrDefaultAsync(u => u.UserName == normalized, cancellationToken)
            ?? throw new UserAdministrationException($"O usuário \"{normalized}\" não existe.");
    }
}
