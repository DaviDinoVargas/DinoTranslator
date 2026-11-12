using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Room;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Room;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

public sealed class IceServersTests : IDisposable
{
    private const string Secret = "segredo-do-coturn-com-mais-de-32-caracteres";

    private readonly ServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Exige_autenticacao()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(RoomRoutes.IceServers);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private WebApplicationFactory<Program> Configured() => _factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Ice:StunUrls:0", "stun:turn.exemplo.com:3478");
        builder.UseSetting("Ice:TurnUrls:0", "turns:turn.exemplo.com:5349?transport=tcp");
        builder.UseSetting("Ice:TurnSecret", Secret);
    });

    private async Task<(Guid Id, HttpClient Http)> PersonAsync(WebApplicationFactory<Program> server, string userName, AccountPlan plan)
    {
        var profile = await _factory.CreateUserAsync(userName);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPlanAsync(userName, plan, null);
        }
        var client = server.CreateClient();
        var tokens = await _factory.LoginAsync(client, userName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (profile.Id, client);
    }

    [Fact]
    public async Task Entrega_stun_e_turn_com_credencial_valida_para_o_coturn()
    {
        using var configured = Configured();
        var (id, client) = await PersonAsync(configured, "dino", AccountPlan.Plus);

        var response = (await client.GetFromJsonAsync<IceServersResponse>(RoomRoutes.IceServers))!;

        Assert.Equal(2, response.IceServers.Count);
        Assert.Null(response.IceServers[0].Credential);
        var turn = response.IceServers[1];
        var expected = _factory.Time.GetUtcNow().AddHours(12);
        Assert.Equal($"{expected.ToUnixTimeSeconds()}:{IceServers.Alias(Secret, id)}", turn.Username);
        Assert.Equal(
            Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(turn.Username!))),
            turn.Credential);
    }

    [Fact]
    public async Task Conexao_garantida_e_so_do_plano_pago()
    {
        using var configured = Configured();
        var (_, free) = await PersonAsync(configured, "gratis", AccountPlan.Free);
        var (_, pro) = await PersonAsync(configured, "pro", AccountPlan.Pro);

        Assert.Equal(HttpStatusCode.Forbidden, (await free.GetAsync(RoomRoutes.IceServers)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pro.GetAsync(RoomRoutes.IceServers)).StatusCode);
    }

    [Fact]
    public void Usuario_do_turn_nao_mostra_a_conta()
    {
        var id = Guid.NewGuid();

        var alias = IceServers.Alias(Secret, id);

        Assert.Equal(32, alias.Length);
        Assert.Equal(alias, IceServers.Alias(Secret, id));
        Assert.NotEqual(alias, IceServers.Alias(Secret, Guid.NewGuid()));
        Assert.NotEqual(alias, IceServers.Alias(Secret + "x", id));
        Assert.DoesNotContain(id.ToString("N")[..8], alias);
    }

    [Fact]
    public void Sem_segredo_nao_entrega_turn()
    {
        var options = new IceOptions { StunUrls = ["stun:a:3478"], TurnUrls = ["turn:a:3478"] };

        var response = IceServers.Create(options, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.All(response.IceServers, server => Assert.Null(server.Credential));
    }

    [Fact]
    public void Servidor_nao_sobe_com_segredo_curto()
    {
        using var invalid = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ice:TurnUrls:0", "turn:a:3478");
            builder.UseSetting("Ice:TurnSecret", "curto");
        });

        Assert.ThrowsAny<Exception>(() => invalid.CreateClient());
    }
}
