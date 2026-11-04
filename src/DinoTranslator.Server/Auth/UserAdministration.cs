using DinoTranslator.Server.Data;
using DinoTranslator.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DinoTranslator.Server.Auth;

public sealed record NewUser(string UserName, string DisplayName, string Language, Gender Gender, string Password);

public sealed class UserAdministrationException(string message) : Exception(message);

/// <summary>
/// Criação e manutenção das contas. Só é usada pelo comando <c>users</c> no próprio
/// servidor — não existe endpoint de cadastro.
/// </summary>
public sealed class UserAdministration(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<AuthOptions> options,
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
            throw new UserAdministrationException($"Idioma inválido: use {string.Join(" ou ", Languages.All)}.");
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
        if (await db.Users.CountAsync(cancellationToken) >= maxUsers)
        {
            throw new UserAdministrationException($"Limite de {maxUsers} contas atingido. Remova uma conta antes de criar outra.");
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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
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
    }

    private async Task<User> FindAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = Credentials.NormalizeUserName(userName);
        return await db.Users.SingleOrDefaultAsync(u => u.UserName == normalized, cancellationToken)
            ?? throw new UserAdministrationException($"O usuário \"{normalized}\" não existe.");
    }
}
