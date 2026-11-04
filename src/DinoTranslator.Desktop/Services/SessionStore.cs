using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DinoTranslator.Desktop.Services;

public interface ISessionStore
{
    void SaveRefreshToken(string refreshToken);

    string? LoadRefreshToken();

    void Clear();
}

/// <summary>
/// Guarda o refresh token cifrado com DPAPI: só o mesmo usuário do Windows, neste
/// computador, consegue decifrar. O token de acesso nunca vai para o disco.
/// </summary>
public sealed class DpapiSessionStore(string path) : ISessionStore
{
    private static readonly byte[] Entropy = "DinoTranslator.session.v1"u8.ToArray();

    public void SaveRefreshToken(string refreshToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);
    }

    public string? LoadRefreshToken()
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            Clear();
            return null;
        }
    }

    public void Clear() => File.Delete(path);
}
