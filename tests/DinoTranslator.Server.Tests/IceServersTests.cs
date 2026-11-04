using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DinoTranslator.Server.Room;
using DinoTranslator.Shared.Room;
using Microsoft.AspNetCore.Hosting;

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

    [Fact]
    public async Task Entrega_stun_e_turn_com_credencial_valida_para_o_coturn()
    {
        using var configured = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ice:StunUrls:0", "stun:turn.exemplo.com:3478");
            builder.UseSetting("Ice:TurnUrls:0", "turns:turn.exemplo.com:5349?transport=tcp");
            builder.UseSetting("Ice:TurnSecret", Secret);
        });
        var profile = await _factory.CreateUserAsync("dino");
        using var client = configured.CreateClient();
        var tokens = await _factory.LoginAsync(client, "dino");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = (await client.GetFromJsonAsync<IceServersResponse>(RoomRoutes.IceServers))!;

        Assert.Equal(2, response.IceServers.Count);
        Assert.Null(response.IceServers[0].Credential);
        var turn = response.IceServers[1];
        var expected = _factory.Time.GetUtcNow().AddHours(12);
        Assert.Equal($"{expected.ToUnixTimeSeconds()}:{profile.Id:N}", turn.Username);
        Assert.Equal(
            Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(turn.Username!))),
            turn.Credential);
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
