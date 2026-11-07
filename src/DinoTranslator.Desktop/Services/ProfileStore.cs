using System.IO;
using System.Text.Json;
using DinoTranslator.Shared;

namespace DinoTranslator.Desktop.Services;

/// <summary>Perfil desta pessoa, guardado em %LOCALAPPDATA%\DinoTranslator\perfil.json.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;

    public ProfileStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public event Action<Profile>? Changed;

    /// <summary><c>null</c> até a pessoa preencher o perfil pela primeira vez.</summary>
    public Profile? Current { get; private set; }

    public void Save(Profile profile)
    {
        if (Profile.Validate(profile) is { } error)
        {
            throw new ArgumentException(error, nameof(profile));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profile, Json));
        File.Move(temporary, _path, overwrite: true);

        Current = profile;
        Changed?.Invoke(profile);
    }

    /// <summary>Fuso deste computador no formato IANA, que funciona igual nos dois lados.</summary>
    public static string LocalTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
        {
            return local.Id;
        }
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "Etc/UTC";
    }

    private Profile? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(_path), Json);
            return Profile.Validate(profile) is null ? profile : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
