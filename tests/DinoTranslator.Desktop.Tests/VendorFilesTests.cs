using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DinoTranslator.Desktop.Tests;

/// <summary>
/// Os supressores de ruído vêm de pacotes de terceiros copiados para o app: cada arquivo tem que
/// ser exatamente o que o ORIGEM.json registra, e nada fora da lista pode estar na pasta.
/// </summary>
public sealed class VendorFilesTests
{
    private static readonly string Vendor = Path.Combine(RepositoryRoot(), "src", "DinoTranslator.Desktop", "wwwroot", "js", "vendor");

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "DinoTranslator.sln")))
            {
                return folder.FullName;
            }
        }
        throw new InvalidOperationException("Pasta do repositório não encontrada.");
    }

    [Fact]
    public void Arquivos_dos_supressores_sao_os_registrados()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Vendor, "ORIGEM.json")));
        var listed = new HashSet<string>(StringComparer.Ordinal) { "ORIGEM.json" };
        foreach (var package in manifest.RootElement.GetProperty("pacotes").EnumerateArray())
        {
            Assert.StartsWith("https://registry.npmjs.org/", package.GetProperty("origem").GetString());
            foreach (var file in package.GetProperty("arquivos").EnumerateObject())
            {
                var path = Path.Combine(Vendor, file.Name.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"falta {file.Name}");
                Assert.Equal(file.Value.GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                listed.Add(file.Name);
            }
        }

        var unlisted = Directory.EnumerateFiles(Vendor, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Vendor, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => !listed.Contains(f))
            .ToList();
        Assert.True(unlisted.Count == 0, "arquivos fora do ORIGEM.json: " + string.Join(", ", unlisted));
    }
}
