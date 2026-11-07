using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Services.Translation;

[JsonConverter(typeof(JsonStringEnumConverter<TranslationProvider>))]
public enum TranslationProvider
{
    None = 0,
    AzureSpeech = 1,

    /// <summary>Reconhecimento e tradução neste computador (Whisper e NLLB), sem conta nem chave.</summary>
    Local = 2,

    /// <summary>Só no build Debug: legendas geradas a partir do volume do áudio, sem serviço externo.</summary>
    Simulated = 99,
}

/// <summary>Motor de reconhecimento e tradução deste computador.</summary>
/// <param name="LocalModel">Só no motor local: modelo de reconhecimento escolhido.</param>
public sealed partial record TranslationSettings(TranslationProvider Provider, string? Region, string? Key, WhisperModel? LocalModel = null)
{
    public static TranslationSettings None { get; } = new(TranslationProvider.None, null, null);

    public bool IsConfigured => Provider != TranslationProvider.None;

    /// <summary>Devolve o motivo da recusa, ou <c>null</c> se a configuração for válida.</summary>
    public static string? Validate(TranslationSettings settings) => settings.Provider switch
    {
        TranslationProvider.None => null,
        TranslationProvider.AzureSpeech when settings.Region is null || !RegionPattern().IsMatch(settings.Region) =>
            "Região inválida. Use o nome que aparece no portal, ex.: brazilsouth.",
        TranslationProvider.AzureSpeech when settings.Key is null || !KeyPattern().IsMatch(settings.Key) =>
            "Chave inválida. Copie uma das chaves do recurso, sem espaços.",
        TranslationProvider.AzureSpeech => null,
        TranslationProvider.Local when settings.LocalModel is not { } model || !Enum.IsDefined(model) =>
            "Escolha o modelo de reconhecimento.",
        TranslationProvider.Local => null,
#if DEBUG
        TranslationProvider.Simulated => null,
#endif
        _ => "Motor de tradução desconhecido.",
    };

    [GeneratedRegex("^[a-z0-9]{2,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();

    [GeneratedRegex("^[A-Za-z0-9]{20,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>
/// Guarda o motor de tradução cifrado com DPAPI: a chave só é legível pelo mesmo usuário do
/// Windows, neste computador, e nunca vai pela chamada.
/// </summary>
public sealed class TranslationSettingsStore
{
    private static readonly byte[] Entropy = "DinoTranslator.motores.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;

    public TranslationSettingsStore(string path)
    {
        _path = path;
        Current = Load();
#if DEBUG
        // Só em desenvolvimento: o teste de ponta a ponta liga o motor simulado por variável.
        if (Environment.GetEnvironmentVariable("DINOTRANSLATOR_ENGINE") == "simulado")
        {
            Current = new TranslationSettings(TranslationProvider.Simulated, null, null);
        }
#endif
    }

    public event Action? Changed;

    public TranslationSettings Current { get; private set; }

    public void Save(TranslationSettings settings)
    {
        if (TranslationSettings.Validate(settings) is { } error)
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
        Changed?.Invoke();
    }

    public void Clear()
    {
        File.Delete(_path);
        Current = TranslationSettings.None;
        Changed?.Invoke();
    }

    private TranslationSettings Load()
    {
        if (!File.Exists(_path))
        {
            return TranslationSettings.None;
        }
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            var settings = JsonSerializer.Deserialize<TranslationSettings>(plain, Json);
            CryptographicOperations.ZeroMemory(plain);
            return settings is not null && TranslationSettings.Validate(settings) is null ? settings : TranslationSettings.None;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return TranslationSettings.None;
        }
    }
}
