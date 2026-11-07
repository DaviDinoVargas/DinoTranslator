using System.IO;
using DinoTranslator.Desktop.Services;

namespace DinoTranslator.Desktop.Tests;

public sealed class ConnectionSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dinotranslator-conexao-{Guid.NewGuid():N}", "conexao.dat");

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_path)))
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("turn:relay.exemplo:3478")]
    [InlineData("turns:relay.exemplo:443?transport=tcp")]
    [InlineData("turn:203.0.113.9:80?transport=udp")]
    public void Retransmissao_valida_e_aceita(string url)
    {
        Assert.Null(ConnectionSettings.Validate(new ConnectionSettings(url, "usuario", "senha")));
    }

    [Theory]
    [InlineData("https://relay.exemplo", "u", "s")]
    [InlineData("turn:relay.exemplo:3478 ; outra", "u", "s")]
    [InlineData("stun:relay.exemplo:3478", "u", "s")]
    [InlineData("turn:relay.exemplo:3478", "", "s")]
    [InlineData("turn:relay.exemplo:3478", "u", null)]
    [InlineData("turn:relay.exemplo:3478", "u", "senha\ncom quebra")]
    [InlineData(null, "u", null)]
    public void Retransmissao_invalida_e_recusada(string? url, string? user, string? password)
    {
        Assert.NotNull(ConnectionSettings.Validate(new ConnectionSettings(url, user, password)));
    }

    [Fact]
    public void Senha_fica_cifrada_no_disco_e_volta_igual()
    {
        var settings = new ConnectionSettings("turn:relay.exemplo:3478", "dino", "senha-secreta-123");
        new ConnectionSettingsStore(_path).Save(settings);

        Assert.DoesNotContain("senha-secreta-123", File.ReadAllText(_path));
        Assert.Equal(settings, new ConnectionSettingsStore(_path).Current);
    }

    [Fact]
    public void Arquivo_adulterado_vira_sem_retransmissao()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, [1, 2, 3]);

        Assert.Equal(ConnectionSettings.None, new ConnectionSettingsStore(_path).Current);
    }

    [Fact]
    public void Retransmissao_vira_servidor_ice_do_webrtc()
    {
        Assert.Null(CallSession.RelayFor(ConnectionSettings.None));
        var relay = CallSession.RelayFor(new ConnectionSettings("turn:relay.exemplo:3478", "dino", "senha"));
        Assert.Equal("{ urls = turn:relay.exemplo:3478, username = dino, credential = senha }", relay?.ToString());
    }
}
