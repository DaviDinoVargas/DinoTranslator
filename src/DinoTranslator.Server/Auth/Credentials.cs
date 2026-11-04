using System.Text.RegularExpressions;

namespace DinoTranslator.Server.Auth;

/// <summary>Regras de nome de usuário e senha.</summary>
public static partial class Credentials
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;
    public const int MaxDisplayNameLength = 64;

    public static string NormalizeUserName(string? userName) => (userName ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidUserName(string normalizedUserName) => UserNamePattern().IsMatch(normalizedUserName);

    /// <summary>Devolve o motivo da recusa, ou <c>null</c> se a senha for aceita.</summary>
    public static string? ValidatePassword(string? password, string normalizedUserName)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return $"A senha precisa ter pelo menos {MinPasswordLength} caracteres.";
        }
        if (password.Length > MaxPasswordLength)
        {
            return $"A senha pode ter no máximo {MaxPasswordLength} caracteres.";
        }
        if (password.Contains(normalizedUserName, StringComparison.OrdinalIgnoreCase))
        {
            return "A senha não pode conter o nome de usuário.";
        }
        if (password.Distinct().Count() < 5)
        {
            return "A senha precisa ter mais variedade de caracteres.";
        }
        return null;
    }

    [GeneratedRegex("^[a-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
}
