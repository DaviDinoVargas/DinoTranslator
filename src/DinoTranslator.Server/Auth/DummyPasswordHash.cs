using System.Security.Cryptography;
using DinoTranslator.Server.Data;
using Microsoft.AspNetCore.Identity;

namespace DinoTranslator.Server.Auth;

/// <summary>
/// Verificação de senha "de mentira" para usuários inexistentes ou bloqueados: o login
/// leva o mesmo tempo em todos os casos e não revela quais usuários existem.
/// </summary>
public sealed class DummyPasswordHash
{
    private readonly IPasswordHasher<User> _hasher;
    private readonly User _user;
    private readonly string _hash;

    public DummyPasswordHash(IPasswordHasher<User> hasher)
    {
        _hasher = hasher;
        _user = new User { UserName = "-", DisplayName = "-", Language = "-", PasswordHash = string.Empty };
        _hash = hasher.HashPassword(_user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
    }

    public void Verify(string password) => _hasher.VerifyHashedPassword(_user, _hash, password);
}
