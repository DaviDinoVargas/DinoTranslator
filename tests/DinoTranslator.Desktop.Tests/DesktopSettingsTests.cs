using DinoTranslator.Desktop.Services;

namespace DinoTranslator.Desktop.Tests;

public class DesktopSettingsTests
{
    [Theory]
    [InlineData(null, ConnectionMode.Code)]
    [InlineData("", ConnectionMode.Code)]
    [InlineData("codigo", ConnectionMode.Code)]
    [InlineData("servidor", ConnectionMode.Server)]
    public void Modo_padrao_e_codigo(string? value, ConnectionMode expected)
    {
        Assert.Equal(expected, DesktopSettings.ParseMode(value));
    }

    [Theory]
    [InlineData("Servidor")]
    [InlineData("p2p")]
    public void Modo_desconhecido_e_recusado(string value)
    {
        Assert.Throws<InvalidOperationException>(() => DesktopSettings.ParseMode(value));
    }

    [Theory]
    [InlineData("https://dino.exemplo.com")]
    [InlineData("https://dino.exemplo.com:8443/")]
    [InlineData("http://localhost:5180")]
    [InlineData("http://127.0.0.1:5180")]
    public void Servidor_aceito(string value)
    {
        Assert.Equal(new Uri(value), DesktopSettings.ParseServerUri(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dino.exemplo.com")]
    [InlineData("http://dino.exemplo.com")]
    [InlineData("https://usuario:senha@dino.exemplo.com")]
    [InlineData("https://dino.exemplo.com/api")]
    [InlineData("https://dino.exemplo.com/?x=1")]
    [InlineData("ftp://dino.exemplo.com")]
    public void Servidor_sem_https_ou_com_caminho_e_recusado(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => DesktopSettings.ParseServerUri(value));
    }
}
