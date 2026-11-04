using DinoTranslator.Server.Auth;
using DinoTranslator.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

public sealed class UserAdministrationTests : IDisposable
{
    private readonly ServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Terceira_conta_e_recusada()
    {
        await _factory.CreateUserAsync("dino");
        await _factory.CreateUserAsync("amiga", language: Languages.Russian);

        var error = await Assert.ThrowsAsync<UserAdministrationException>(() => _factory.CreateUserAsync("terceiro"));

        Assert.Contains("Limite de 2 contas", error.Message);
    }

    [Fact]
    public async Task Usuario_repetido_e_recusado_mesmo_com_maiusculas()
    {
        await _factory.CreateUserAsync("dino");

        await Assert.ThrowsAsync<UserAdministrationException>(() => _factory.CreateUserAsync("DINO"));
    }

    [Theory]
    [InlineData("curta-123")]
    [InlineData("minha-senha-dino-2026")]
    [InlineData("aaaabbbbaaaabbbb")]
    public async Task Senha_fraca_e_recusada(string password)
    {
        await Assert.ThrowsAsync<UserAdministrationException>(() => _factory.CreateUserAsync("dino", password));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("nome com espaco")]
    [InlineData("usuário")]
    [InlineData("um-nome-de-usuario-longo-demais-aqui")]
    public async Task Nome_de_usuario_invalido_e_recusado(string userName)
    {
        await Assert.ThrowsAsync<UserAdministrationException>(() => _factory.CreateUserAsync(userName));
    }

    [Fact]
    public async Task Idioma_nao_suportado_e_recusado()
    {
        await Assert.ThrowsAsync<UserAdministrationException>(() => _factory.CreateUserAsync("dino", language: "en-US"));
    }

    [Fact]
    public async Task Remover_conta_libera_a_vaga()
    {
        await _factory.CreateUserAsync("dino");
        await _factory.CreateUserAsync("amiga");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().RemoveAsync("amiga");
        }

        await _factory.CreateUserAsync("outra");
    }
}
