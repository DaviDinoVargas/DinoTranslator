using Microsoft.IdentityModel.Tokens;

namespace DinoTranslator.Server.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "dinotranslator";

    public string Audience { get; set; } = "dinotranslator-desktop";

    /// <summary>
    /// Chave HMAC em base64 com pelo menos 32 bytes. Vem de user-secrets ou variável
    /// de ambiente (<c>Jwt__SigningKey</c>) — nunca do repositório.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public bool HasValidKey() => TryDecodeKey(out _);

    public SymmetricSecurityKey CreateSecurityKey() =>
        TryDecodeKey(out var key)
            ? new SymmetricSecurityKey(key)
            : throw new InvalidOperationException("Jwt:SigningKey ausente ou com menos de 256 bits.");

    private bool TryDecodeKey(out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(SigningKey))
        {
            return false;
        }
        try
        {
            key = Convert.FromBase64String(SigningKey);
        }
        catch (FormatException)
        {
            return false;
        }
        return key.Length >= 32;
    }
}
