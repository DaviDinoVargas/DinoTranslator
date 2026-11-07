using System.IO;
using System.Security.Cryptography;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Tests;

public sealed class ModelFolderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dinotranslator-pasta-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, recursive: true);
        }
    }

    [Fact]
    public void Apagar_os_modelos_nao_toca_em_outros_arquivos_da_pasta()
    {
        Directory.CreateDirectory(_path);
        var folder = new ModelFolder(_path);
        File.WriteAllText(folder.PathOf(LocalModels.WhisperSmall), "x");
        File.WriteAllText(Path.Combine(_path, "documento-da-pessoa.txt"), "importante");

        folder.Remove();

        Assert.False(File.Exists(folder.PathOf(LocalModels.WhisperSmall)));
        Assert.True(File.Exists(Path.Combine(_path, "documento-da-pessoa.txt")));
    }

    [Fact]
    public void Pasta_vazia_depois_de_apagar_some_e_pasta_inexistente_nao_e_erro()
    {
        Directory.CreateDirectory(_path);
        var folder = new ModelFolder(_path);
        File.WriteAllText(folder.PathOf(LocalModels.TranslatorVocabulary), "x");

        folder.Remove();
        folder.Remove();

        Assert.False(Directory.Exists(_path));
    }

    [Fact]
    public void Modelo_alterado_depois_do_download_e_recusado_ao_carregar()
    {
        Directory.CreateDirectory(_path);
        var content = RandomNumberGenerator.GetBytes(10_000);
        var file = new ModelFile("modelo.bin", new Uri("https://exemplo/modelo.bin"), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
        var folder = new ModelFolder(_path);
        File.WriteAllBytes(folder.PathOf(file), content);

        folder.Verify(file);

        content[42] ^= 0xFF;
        File.WriteAllBytes(folder.PathOf(file), content);
        File.SetLastWriteTimeUtc(folder.PathOf(file), DateTime.UtcNow.AddMinutes(1));
        var error = Assert.Throws<ModelDownloadException>(() => folder.Verify(file));
        Assert.Contains("alterado", error.Message);
    }

    [Fact]
    public void Modelo_ausente_ou_de_tamanho_errado_e_recusado()
    {
        var folder = new ModelFolder(_path);

        Assert.Throws<ModelDownloadException>(() => folder.Verify(LocalModels.WhisperSmall));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("modelos")]
    [InlineData(@"..\modelos")]
    [InlineData(@"\\servidor\compartilhado\modelos")]
    [InlineData(@"Q:\nao-existe\modelos")]
    public void Pasta_invalida_e_recusada(string? path)
    {
        Assert.NotNull(ModelFolder.ValidatePath(path));
    }

    [Fact]
    public void Pasta_num_disco_fixo_e_aceita()
    {
        Assert.Null(ModelFolder.ValidatePath(_path));
        Assert.NotNull(new ModelFolder(Path.GetTempPath()).FreeSpace());
    }
}
