using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DinoTranslator.Desktop.Services;

/// <summary>
/// Retransmissão (TURN) opcional, para redes que não deixam a ligação direta passar
/// (NAT restritivo da operadora sem IPv6 dos dois lados). Os dados da chamada continuam
/// cifrados de ponta a ponta: a retransmissão só repassa pacotes.
/// </summary>
/// <param name="TurnUrl">Ex.: <c>turn:servidor.exemplo:3478</c> ou <c>turns:servidor.exemplo:443?transport=tcp</c>.</param>
public sealed partial record ConnectionSettings(string? TurnUrl, string? TurnUser, string? TurnPassword)
{
    public const int MaxFieldLength = 256;

    public static ConnectionSettings None { get; } = new(null, null, null);

    public bool HasRelay => TurnUrl is not null;

    /// <summary>Mensagem para a pessoa, ou <c>null</c> se estiver tudo certo.</summary>
    public static string? Validate(ConnectionSettings settings)
    {
        if (!settings.HasRelay)
        {
            return settings.TurnUser is null && settings.TurnPassword is null ? null : "Informe o endereço da retransmissão.";
        }
        if (!TurnUrlPattern().IsMatch(settings.TurnUrl!))
        {
            return "Endereço inválido. Use o formato turn:servidor:porta (ou turns: para TLS).";
        }
        if (string.IsNullOrEmpty(settings.TurnUser) || string.IsNullOrEmpty(settings.TurnPassword))
        {
            return "Informe usuário e senha da retransmissão.";
        }
        if (!IsSafeText(settings.TurnUser) || !IsSafeText(settings.TurnPassword))
        {
            return "Usuário ou senha com caracteres inválidos.";
        }
        return null;
    }

    private static bool IsSafeText(string value) =>
        value.Length <= MaxFieldLength && value.All(c => c is >= ' ' and <= '~');

    [GeneratedRegex(@"^turns?:[A-Za-z0-9.\-]{1,200}(:[0-9]{1,5})?(\?transport=(udp|tcp))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TurnUrlPattern();
}

/// <summary>Guarda a retransmissão cifrada com DPAPI (a senha só é legível por este usuário do Windows).</summary>
public sealed class ConnectionSettingsStore
{
    private static readonly byte[] Entropy = "DinoTranslator.conexao.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;

    public ConnectionSettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public ConnectionSettings Current { get; private set; }

    public void Save(ConnectionSettings settings)
    {
        if (ConnectionSettings.Validate(settings) is { } error)
        {
            throw new ArgumentException(error, nameof(settings));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temporary, _path, overwrite: true);
        CryptographicOperations.ZeroMemory(plain);
        Current = settings;
    }

    public void Clear()
    {
        File.Delete(_path);
        Current = ConnectionSettings.None;
    }

    private ConnectionSettings Load()
    {
        if (!File.Exists(_path))
        {
            return ConnectionSettings.None;
        }
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            var settings = JsonSerializer.Deserialize<ConnectionSettings>(plain, Json);
            CryptographicOperations.ZeroMemory(plain);
            return settings is not null && ConnectionSettings.Validate(settings) is null ? settings : ConnectionSettings.None;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return ConnectionSettings.None;
        }
    }
}
