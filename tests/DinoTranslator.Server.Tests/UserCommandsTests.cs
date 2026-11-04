using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Cli;
using DinoTranslator.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

public sealed class UserCommandsTests : IDisposable
{
    private readonly ServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Add_cria_a_conta_com_idioma_e_genero()
    {
        var (exitCode, output) = await RunAsync(
            ["add", "amiga", "--nome", "Amiga", "--idioma", "ru-RU", "--genero", "feminino"],
            interactive: true, "senha-de-teste-forte", "senha-de-teste-forte");

        Assert.Equal(0, exitCode);
        Assert.Contains("Conta \"amiga\" criada.", output);
        var user = Assert.Single(await ListUsersAsync());
        Assert.Equal(Languages.Russian, user.Language);
        Assert.Equal(Gender.Female, user.Gender);
    }

    [Fact]
    public async Task Senhas_diferentes_na_confirmacao_nao_criam_conta()
    {
        var (exitCode, _) = await RunAsync(
            ["add", "dino", "--nome", "Dino", "--idioma", "pt-BR"],
            interactive: true, "senha-de-teste-forte", "outra-senha-diferente");

        Assert.Equal(1, exitCode);
        Assert.Empty(await ListUsersAsync());
    }

    [Fact]
    public async Task Entrada_por_pipe_le_a_senha_uma_vez()
    {
        var (exitCode, _) = await RunAsync(
            ["add", "dino", "--nome", "Dino", "--idioma", "pt-BR"],
            interactive: false, "senha-de-teste-forte");

        Assert.Equal(0, exitCode);
        Assert.Single(await ListUsersAsync());
    }

    [Theory]
    [InlineData("add", "dino")]
    [InlineData("add", "dino", "--nome", "Dino")]
    [InlineData("add", "dino", "--nome", "Dino", "--idioma", "pt-BR", "--genero", "outro")]
    [InlineData("apagar-tudo")]
    public async Task Argumentos_invalidos_mostram_o_uso(params string[] args)
    {
        var (exitCode, output) = await RunAsync(args, interactive: false, "senha-de-teste-forte");

        Assert.Equal(2, exitCode);
        Assert.Contains("Uso:", output);
    }

    [Fact]
    public async Task Erro_de_validacao_e_mostrado_sem_excecao()
    {
        var (exitCode, output) = await RunAsync(
            ["add", "dino", "--nome", "Dino", "--idioma", "en-US"],
            interactive: false, "senha-de-teste-forte");

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Erro: Idioma inválido", output);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string[] args, bool interactive, params string[] secrets)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        using var output = new StringWriter();
        var commands = new UserCommands(
            scope.ServiceProvider.GetRequiredService<UserAdministration>(),
            output,
            new FakeSecretReader(interactive, secrets));
        var exitCode = await commands.RunAsync(args);
        return (exitCode, output.ToString());
    }

    private async Task<List<Data.User>> ListUsersAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserAdministration>().ListAsync();
    }

    private sealed class FakeSecretReader(bool interactive, params string[] secrets) : ISecretReader
    {
        private readonly Queue<string> _secrets = new(secrets);

        public bool IsInteractive => interactive;

        public string? ReadSecret(string prompt) => _secrets.TryDequeue(out var secret) ? secret : null;
    }
}
