using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DinoTranslator.Installation;

namespace DinoTranslator.Desktop.Tests;

/// <summary>Instalação dos motores extras com um servidor em memória: nada sai do computador.</summary>
public sealed class ExtrasInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DinoTranslator.Extras", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Baixa_confere_e_extrai_na_pasta_do_componente()
    {
        var zip = Zip(("model.bin", "pesos"), ("config.json", "{}"));
        var model = Component("ct2-teste", "ct2/teste", zip);
        var file = Component("arquivo", "modelos", "abc"u8.ToArray(), ExtraKind.File, "ggml-teste.bin");
        using var http = Serve(("ct2-teste", zip), ("arquivo", "abc"u8.ToArray()));

        await ExtrasInstaller.InstallAsync(_root, [model, file], http, null, CancellationToken.None);

        Assert.Equal("pesos", File.ReadAllText(Path.Combine(_root, "ct2", "teste", "model.bin")));
        Assert.Equal("abc", File.ReadAllText(Path.Combine(_root, "modelos", "ggml-teste.bin")));
        Assert.True(ExtrasInstaller.IsInstalled(_root, model));
        Assert.True(ExtrasInstaller.IsInstalled(_root, file));
        Assert.Empty(ExtrasInstaller.Missing(_root, [model, file]));
        Assert.False(Directory.Exists(Path.Combine(_root, ".baixando")));
    }

    [Fact]
    public async Task Arquivo_diferente_do_catalogo_e_descartado()
    {
        var expected = Zip(("model.bin", "pesos"));
        var component = Component("ct2-teste", "ct2/teste", expected);
        using var http = Serve(("ct2-teste", Zip(("model.bin", "outro"))));

        await Assert.ThrowsAsync<InstallationException>(() => ExtrasInstaller.InstallAsync(_root, [component], http, null, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_root, "ct2", "teste")));
        Assert.False(ExtrasInstaller.IsInstalled(_root, component));
    }

    [Fact]
    public async Task Zip_que_tenta_sair_da_pasta_e_recusado()
    {
        var zip = Zip(("../../fora.txt", "x"));
        var component = Component("malicioso", "ct2/teste", zip);
        using var http = Serve(("malicioso", zip));

        await Assert.ThrowsAsync<InstallationException>(() => ExtrasInstaller.InstallAsync(_root, [component], http, null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "..", "fora.txt")));
        Assert.False(ExtrasInstaller.IsInstalled(_root, component));
    }

    [Fact]
    public void Catalogo_tem_enderecos_https_tamanhos_e_assinaturas()
    {
        Assert.NotEmpty(ExtrasCatalog.All);
        Assert.All(ExtrasCatalog.All, c =>
        {
            Assert.Equal(Uri.UriSchemeHttps, c.Source.Scheme);
            Assert.InRange(c.Size, 1, 2L * 1024 * 1024 * 1024 - 1);
            Assert.Matches("^[0-9a-f]{64}$", c.Sha256);
            Assert.False(Path.IsPathRooted(c.Folder));
            Assert.DoesNotContain("..", c.Folder);
        });
        Assert.Equal(ExtrasCatalog.All.Count, ExtrasCatalog.All.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void Modelos_originais_do_catalogo_sao_os_mesmos_do_app()
    {
        foreach (var file in DinoTranslator.Desktop.Services.Translation.Local.LocalModels.All)
        {
            var component = Assert.Single(ExtrasCatalog.All, c => c.FileName == file.FileName);
            Assert.Equal((file.Source, file.Size, file.Sha256), (component.Source, component.Size, component.Sha256));
        }
    }

    private static ExtraComponent Component(string name, string folder, byte[] content, ExtraKind kind = ExtraKind.Zip, string? fileName = null) =>
        new(name, name, new Uri($"https://extras.exemplo/{name}"), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), folder, kind, fileName);

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }

    private static HttpClient Serve(params (string Name, byte[] Content)[] files) =>
        new(new MemoryServer(files.ToDictionary(f => "/" + f.Name, f => f.Content)));

    private sealed class MemoryServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
