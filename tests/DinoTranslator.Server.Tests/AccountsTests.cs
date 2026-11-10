using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Data;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

/// <summary>Cadastro pela rede, planos e suspensão de contas.</summary>
public sealed class AccountsTests : IDisposable
{
    private readonly ServerFactory _factory = new();
    private readonly HttpClient _client;

    public AccountsTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static RegisterRequest NewAccount(string userName = "dino", string? code = null) =>
        new(userName, ServerFactory.DefaultPassword, "Dino", Languages.PortugueseBrazil, Gender.Male, code);

    [Fact]
    public async Task Cadastro_cria_conta_gratis_e_ja_entra()
    {
        var response = await _client.PostAsJsonAsync(AuthRoutes.Register, NewAccount());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal("dino", tokens.User.UserName);
        Assert.Equal(AccountPlan.Free, tokens.User.Plan);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(_client, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Cadastro_recusa_nome_repetido_e_senha_fraca()
    {
        await _client.PostAsJsonAsync(AuthRoutes.Register, NewAccount());

        var repeated = await _client.PostAsJsonAsync(AuthRoutes.Register, NewAccount("DINO"));
        var weak = await _client.PostAsJsonAsync(AuthRoutes.Register, NewAccount("outra") with { Password = "123" });

        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Contains("já existe", await repeated.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    [Fact]
    public async Task Cadastro_fechado_recusa()
    {
        using var closed = new ServerFactory(new Dictionary<string, string?> { ["Auth:OpenRegistration"] = "false" });
        using var client = closed.CreateClient();

        var response = await client.PostAsJsonAsync(AuthRoutes.Register, NewAccount());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cadastro_com_codigo_exige_o_codigo_certo()
    {
        using var coded = new ServerFactory(new Dictionary<string, string?> { ["Auth:RegistrationCode"] = "codigo-secreto-123" });
        using var client = coded.CreateClient();

        var wrong = await client.PostAsJsonAsync(AuthRoutes.Register, NewAccount(code: "codigo-errado-0000"));
        var missing = await client.PostAsJsonAsync(AuthRoutes.Register, NewAccount());
        var right = await client.PostAsJsonAsync(AuthRoutes.Register, NewAccount(code: "codigo-secreto-123"));

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Cadastro_tem_limite_por_ip()
    {
        using var limited = new ServerFactory(new Dictionary<string, string?> { ["Auth:RegisterPermitsPerHour"] = "2" });
        using var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        foreach (var name in new[] { "um-usuario", "dois-usuario", "tres-usuario" })
        {
            statuses.Add((await client.PostAsJsonAsync(AuthRoutes.Register, NewAccount(name))).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Plano_plus_vai_no_token_com_o_prazo_e_renovacao_soma()
    {
        await _factory.CreateUserAsync("dino");
        var free = await _factory.LoginAsync(_client, "dino");
        Assert.False(Claims(free.AccessToken).TryGetProperty(AuthClaims.Plan, out _));

        await WithAdminAsync(admin => admin.SetPlanAsync("dino", AccountPlan.Plus, 30));
        var first = await WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.UserName == "dino"));
        await WithAdminAsync(admin => admin.SetPlanAsync("dino", AccountPlan.Plus, 30));
        var renewed = await WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.UserName == "dino"));
        var plus = await _factory.LoginAsync(_client, "dino");

        Assert.Equal(first.PlanExpiresAtUtc!.Value.AddDays(30), renewed.PlanExpiresAtUtc!.Value, TimeSpan.FromSeconds(1));
        var claims = Claims(plus.AccessToken);
        Assert.Equal("plus", claims.GetProperty(AuthClaims.Plan).GetString());
        Assert.True(claims.TryGetProperty(AuthClaims.PlanUntil, out _));
        Assert.Equal(AccountPlan.Plus, plus.User.Plan);
    }

    [Fact]
    public async Task Plano_vencido_nao_vai_no_token()
    {
        await _factory.CreateUserAsync("dino");
        await WithAdminAsync(admin => admin.SetPlanAsync("dino", AccountPlan.Plus, 1));
        _factory.Time.Advance(TimeSpan.FromDays(2));

        var tokens = await _factory.LoginAsync(_client, "dino");

        Assert.False(Claims(tokens.AccessToken).TryGetProperty(AuthClaims.Plan, out _));
    }

    [Fact]
    public async Task Conta_suspensa_perde_as_sessoes_e_nao_entra()
    {
        await _factory.CreateUserAsync("dino");
        var tokens = await _factory.LoginAsync(_client, "dino");
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(_client, tokens.AccessToken)).StatusCode);

        await WithAdminAsync(admin => admin.DisableAsync("dino"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(_client, tokens.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(AuthRoutes.Login, new LoginRequest("dino", ServerFactory.DefaultPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(AuthRoutes.Refresh, new RefreshRequest(tokens.RefreshToken))).StatusCode);

        await WithAdminAsync(admin => admin.EnableAsync("dino"));
        await _factory.LoginAsync(_client, "dino");
    }

    [Fact]
    public async Task Senha_nova_usa_210_mil_iteracoes()
    {
        await _factory.CreateUserAsync("dino");

        var user = await WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.UserName == "dino"));
        var hash = Convert.FromBase64String(user.PasswordHash);

        // Formato V3: versão (1 byte), PRF (4), iterações (4, big-endian), ...
        Assert.Equal(0x01, hash[0]);
        Assert.Equal(210_000u, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5, 4)));
    }

    private static Task<HttpResponseMessage> GetMeAsync(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AuthRoutes.Me);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }

    private static JsonElement Claims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }

    private async Task WithAdminAsync(Func<UserAdministration, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<UserAdministration>());
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
