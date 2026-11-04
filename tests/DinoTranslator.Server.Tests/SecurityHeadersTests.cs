using System.Net;

namespace DinoTranslator.Server.Tests;

public class SecurityHeadersTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Health_responde_ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "no-referrer")]
    [InlineData("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'")]
    public async Task Respostas_trazem_cabecalhos_de_seguranca(string header, string expected)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.True(response.Headers.TryGetValues(header, out var values), $"Cabeçalho {header} ausente.");
        Assert.Equal(expected, Assert.Single(values));
    }

    [Fact]
    public async Task Rota_inexistente_tambem_traz_cabecalhos()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/nao-existe");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }
}
