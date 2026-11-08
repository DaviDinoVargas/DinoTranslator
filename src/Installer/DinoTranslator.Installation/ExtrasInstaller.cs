using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace DinoTranslator.Installation;

public enum ExtraKind
{
    /// <summary>Zip extraído numa pasta própria dentro de "extras".</summary>
    Zip,

    /// <summary>Arquivo único (os modelos originais do Whisper e do NLLB), copiado como está.</summary>
    File,
}

/// <summary>Componente dos motores extras: endereço fixo, tamanho e SHA-256 conferidos ao baixar.</summary>
/// <param name="Folder">Relativa à pasta "extras": onde o zip é extraído ou onde o arquivo fica.</param>
public sealed record ExtraComponent(string Name, string Title, Uri Source, long Size, string Sha256, string Folder, ExtraKind Kind, string? FileName = null);

public sealed record ExtrasProgress(string Title, long DoneBytes, long TotalBytes);

/// <summary>
/// Baixa e instala os motores extras na pasta "extras" ao lado do app: tudo por HTTPS, conferido
/// pelo tamanho e pelo SHA-256 do catálogo antes de ser extraído, e extraído só dentro da pasta.
/// </summary>
public static class ExtrasInstaller
{
    public const string FolderName = "extras";
    private const string Marker = ".componente";
    private const long Spare = 500L * 1024 * 1024;

    public static string RootFor(string appFolder) => Path.Combine(appFolder, FolderName);

    public static bool IsInstalled(string root, ExtraComponent component)
    {
        if (component.Kind == ExtraKind.File)
        {
            var file = new FileInfo(Path.Combine(root, component.Folder, component.FileName!));
            return file.Exists && file.Length == component.Size;
        }
        var marker = Path.Combine(root, component.Folder, Marker);
        return File.Exists(marker) && File.ReadAllText(marker).Trim() == component.Sha256;
    }

    public static IReadOnlyList<ExtraComponent> Missing(string root, IReadOnlyList<ExtraComponent>? components = null) =>
        (components ?? ExtrasCatalog.All).Where(c => !IsInstalled(root, c)).ToList();

    /// <summary>Espaço que falta no disco para instalar (zip + extraído), ou 0 se couber.</summary>
    public static long MissingSpace(string root, IReadOnlyList<ExtraComponent> components)
    {
        var needed = components.Sum(c => c.Kind == ExtraKind.Zip ? c.Size * 2 : c.Size) + Spare;
        var drive = Path.GetPathRoot(Path.GetFullPath(root));
        var free = drive is null ? 0 : new DriveInfo(drive).AvailableFreeSpace;
        return Math.Max(0, needed - free);
    }

    public static async Task InstallAsync(
        string root, IReadOnlyList<ExtraComponent> components, HttpClient http, IProgress<ExtrasProgress>? progress, CancellationToken cancellationToken)
    {
        if (MissingSpace(root, components) is > 0 and var missing)
        {
            throw new InstallationException($"Faltam {missing / 1_000_000_000.0:0.0} GB livres no disco para os motores extras.");
        }
        var staging = Path.Combine(root, ".baixando");
        Directory.CreateDirectory(staging);
        var total = components.Sum(c => c.Size);
        long done = 0;
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = Path.Combine(staging, component.Name + ".part");
            var before = done;
            await DownloadAsync(http, component, part, bytes => progress?.Report(new ExtrasProgress(component.Title, before + bytes, total)), cancellationToken);
            if (component.Kind == ExtraKind.File)
            {
                var destination = Path.Combine(root, component.Folder, component.FileName!);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(part, destination, overwrite: true);
            }
            else
            {
                var target = Path.Combine(root, component.Folder);
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                ExtractInside(part, target);
                File.WriteAllText(Path.Combine(target, Marker), component.Sha256);
                File.Delete(part);
            }
            done += component.Size;
            progress?.Report(new ExtrasProgress(component.Title, done, total));
        }
        if (!Directory.EnumerateFileSystemEntries(staging).Any())
        {
            Directory.Delete(staging);
        }
    }

    private static async Task DownloadAsync(HttpClient http, ExtraComponent component, string path, Action<long> progress, CancellationToken cancellationToken)
    {
        if (component.Source.Scheme != Uri.UriSchemeHttps)
        {
            throw new InstallationException("Endereço de download inválido.");
        }
        using var response = await http.GetAsync(component.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InstallationException($"Não foi possível baixar {component.Title} (HTTP {(int)response.StatusCode}).");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        var buffer = new byte[1024 * 1024];
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received += read;
                if (received > component.Size)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress(received);
            }
        }
        if (received != component.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != component.Sha256)
        {
            File.Delete(path);
            throw new InstallationException($"{component.Title} chegou diferente do esperado e foi descartado. Tente de novo.");
        }
    }

    /// <summary>Extrai sem deixar nenhuma entrada sair da pasta de destino (zip adulterado).</summary>
    private static void ExtractInside(string zip, string target)
    {
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallationException("Pacote dos motores extras inválido.");
            }
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }
}
