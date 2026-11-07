using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DinoTranslator.Desktop.Services.Translation.Local;

namespace DinoTranslator.Desktop.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
    private static readonly byte[] Content = RandomNumberGenerator.GetBytes(300_000);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dinotranslator-modelos-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static ModelFile File(string name = "modelo.bin", byte[]? content = null, string scheme = "https") =>
        new(name, new Uri($"{scheme}://modelos.exemplo/{name}"), (content ?? Content).Length, Convert.ToHexStringLower(SHA256.HashData(content ?? Content)));

    [Fact]
    public async Task Arquivo_certo_e_instalado_e_progresso_chega_ao_total()
    {
        var folder = new ModelFolder(_folder);
        var reports = new List<DownloadProgress>();
        var downloader = new ModelDownloader(new HttpClient(new FakeHandler(Content)), folder);

        await downloader.DownloadAsync([File()], new SyncProgress(reports.Add), CancellationToken.None);

        Assert.True(folder.IsInstalled(File()));
        Assert.Equal(Content.Length, reports[^1].Downloaded);
        Assert.Equal(Content.Length, reports[^1].Total);
        Assert.Empty(Directory.GetFiles(_folder, "*.part"));
    }

    [Fact]
    public async Task Assinatura_diferente_e_recusada_e_nada_fica_no_disco()
    {
        var folder = new ModelFolder(_folder);
        var tampered = (byte[])Content.Clone();
        tampered[1234] ^= 0xFF;
        var downloader = new ModelDownloader(new HttpClient(new FakeHandler(tampered)), folder);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAsync([File()], null, CancellationToken.None));

        Assert.Contains("assinatura", error.Message);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Arquivo_maior_que_o_esperado_e_recusado()
    {
        var folder = new ModelFolder(_folder);
        var downloader = new ModelDownloader(new HttpClient(new FakeHandler([.. Content, .. new byte[10]])), folder);

        await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAsync([File()], null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Arquivo_incompleto_e_recusado()
    {
        var folder = new ModelFolder(_folder);
        var downloader = new ModelDownloader(new HttpClient(new FakeHandler(Content[..1000])), folder);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAsync([File()], null, CancellationToken.None));

        Assert.Contains("incompleto", error.Message);
    }

    [Fact]
    public async Task Erro_do_servidor_vira_mensagem()
    {
        var downloader = new ModelDownloader(new HttpClient(new FakeHandler(Content, HttpStatusCode.NotFound)), new ModelFolder(_folder));

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadAsync([File()], null, CancellationToken.None));

        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task Arquivo_ja_instalado_nao_e_baixado_de_novo()
    {
        var folder = new ModelFolder(_folder);
        Directory.CreateDirectory(_folder);
        await System.IO.File.WriteAllBytesAsync(folder.PathOf(File()), Content);
        var handler = new FakeHandler(Content);

        await new ModelDownloader(new HttpClient(handler), folder).DownloadAsync([File()], null, CancellationToken.None);

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Endereco_sem_https_e_recusado()
    {
        var downloader = new ModelDownloader(new HttpClient(new FakeHandler(Content)), new ModelFolder(_folder));

        await Assert.ThrowsAsync<ModelDownloadException>(() =>
            downloader.DownloadAsync([File(scheme: "http")], null, CancellationToken.None));
    }

    [Fact]
    public void Catalogo_aponta_para_versoes_fixas_com_https_e_sha256()
    {
        foreach (var model in Enum.GetValues<WhisperModel>())
        {
            Assert.All(LocalModels.Required(model), file =>
            {
                Assert.Equal(Uri.UriSchemeHttps, file.Source.Scheme);
                Assert.Equal("huggingface.co", file.Source.Host);
                Assert.DoesNotContain("/resolve/main/", file.Source.AbsoluteUri);
                Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
                Assert.True(file.Size > 1_000_000);
            });
        }
        Assert.True(LocalModels.DownloadSize(WhisperModel.Small) < LocalModels.DownloadSize(WhisperModel.Turbo));
    }

    private sealed class FakeHandler(byte[] content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
