using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DinoTranslator.Server.Auth;
using DinoTranslator.Shared.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly ServerFactory _factory = new();
    private readonly HttpClient _client;

    public AuthTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Login_valido_retorna_tokens_e_perfil()
    {
        await _factory.CreateUserAsync("dino");

        var tokens = await _factory.LoginAsync(_client, "  Dino ");

        Assert.Equal("dino", tokens.User.UserName);
        Assert.NotEmpty(tokens.AccessToken);
        Assert.NotEmpty(tokens.RefreshToken);
        Assert.Equal(_factory.Time.GetUtcNow().AddMinutes(10), tokens.AccessTokenExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Senha_errada_e_usuario_inexistente_recebem_a_mesma_resposta()
    {
        await _factory.CreateUserAsync("dino");

        var wrongPassword = await PostLoginAsync("dino", "outra-senha-qualquer");
        var unknownUser = await PostLoginAsync("ninguem", ServerFactory.DefaultPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await ProblemTitleAsync(wrongPassword), await ProblemTitleAsync(unknownUser));
    }

    [Fact]
    public async Task Conta_bloqueia_apos_cinco_falhas_e_libera_depois_do_prazo()
    {
        await _factory.CreateUserAsync("dino");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync("dino", "senha-errada-123")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync("dino", ServerFactory.DefaultPassword)).StatusCode);

        _factory.Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync("dino", ServerFactory.DefaultPassword)).StatusCode);
    }

    [Fact]
    public async Task Refresh_entrega_tokens_novos_e_o_antigo_deixa_de_valer()
    {
        await _factory.CreateUserAsync("dino");
        var first = await _factory.LoginAsync(_client, "dino");

        var refreshed = await PostRefreshAsync(first.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await refreshed.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await PostRefreshAsync(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Reuso_de_refresh_token_revoga_a_sessao_inteira()
    {
        await _factory.CreateUserAsync("dino");
        var first = await _factory.LoginAsync(_client, "dino");
        var second = (await (await PostRefreshAsync(first.RefreshToken)).Content.ReadFromJsonAsync<TokenResponse>())!;

        var reuse = await PostRefreshAsync(first.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Sessoes_diferentes_sao_independentes()
    {
        await _factory.CreateUserAsync("dino");
        var desktop = await _factory.LoginAsync(_client, "dino");
        var other = await _factory.LoginAsync(_client, "dino");

        await _client.PostAsJsonAsync(AuthRoutes.Logout, new RefreshRequest(desktop.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(desktop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostRefreshAsync(other.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Api_me_exige_token_valido()
    {
        var profile = await _factory.CreateUserAsync("dino");
        var tokens = await _factory.LoginAsync(_client, "dino");

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync("nao-e-um-jwt")).StatusCode);

        var me = await GetMeAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(profile, await me.Content.ReadFromJsonAsync<UserProfile>());
    }

    [Fact]
    public async Task Token_de_acesso_expira_em_quinze_minutos()
    {
        await _factory.CreateUserAsync("dino");
        var tokens = await _factory.LoginAsync(_client, "dino");

        _factory.Time.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Token_assinado_com_outra_chave_e_recusado()
    {
        await _factory.CreateUserAsync("dino");
        using var otherServer = new ServerFactory();
        await otherServer.CreateUserAsync("dino");
        using var otherClient = otherServer.CreateClient();
        var foreign = await otherServer.LoginAsync(otherClient, "dino");

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(foreign.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Troca_de_senha_invalida_tokens_ja_emitidos()
    {
        await _factory.CreateUserAsync("dino");
        var tokens = await _factory.LoginAsync(_client, "dino");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().ResetPasswordAsync("dino", "nova-senha-bem-forte");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(tokens.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(tokens.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync("dino", "nova-senha-bem-forte")).StatusCode);
    }

    [Fact]
    public async Task Excesso_de_tentativas_de_login_recebe_429()
    {
        using var limited = _factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:LoginPermitsPerMinute", "3"));
        using var client = limited.CreateClient();
        var request = new LoginRequest("ninguem", "qualquer-senha-123");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(AuthRoutes.Login, request)).StatusCode);
        }

        var blocked = await client.PostAsJsonAsync(AuthRoutes.Login, request);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
    }

    [Fact]
    public async Task Requisicao_grande_demais_e_recusada()
    {
        var huge = new LoginRequest("dino", new string('x', 100_000));

        var response = await _client.PostAsJsonAsync(AuthRoutes.Login, huge);

        Assert.False(response.IsSuccessStatusCode);
    }

    private Task<HttpResponseMessage> PostLoginAsync(string userName, string password) =>
        _client.PostAsJsonAsync(AuthRoutes.Login, new LoginRequest(userName, password));

    private Task<HttpResponseMessage> PostRefreshAsync(string refreshToken) =>
        _client.PostAsJsonAsync(AuthRoutes.Refresh, new RefreshRequest(refreshToken));

    private Task<HttpResponseMessage> GetMeAsync(string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AuthRoutes.Me);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return _client.SendAsync(request);
    }

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("title").GetString();
    }
}
