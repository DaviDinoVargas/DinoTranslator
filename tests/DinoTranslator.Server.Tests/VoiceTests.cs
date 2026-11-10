using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Calls;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DinoTranslator.Server.Tests;

/// <summary>Chamadas em grupo: permissão para a sala do canal e webhooks assinados do LiveKit.</summary>
public sealed class VoiceTests : IDisposable
{
    private const string Key = "chave-de-teste";
    private const string Secret = "segredo-de-teste-com-mais-de-32-caracteres";

    private readonly ServerFactory _factory = new(new Dictionary<string, string?>
    {
        ["LiveKit:Url"] = "wss://midia.exemplo.test",
        ["LiveKit:ApiKey"] = Key,
        ["LiveKit:ApiSecret"] = Secret,
    });

    public void Dispose() => _factory.Dispose();

    private async Task<(Guid Id, HttpClient Http)> PlusAsync(string userName)
    {
        var profile = await _factory.CreateUserAsync(userName);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPlanAsync(userName, AccountPlan.Plus, null);
        }
        var http = _factory.CreateClient();
        var tokens = await _factory.LoginAsync(http, userName);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (profile.Id, http);
    }

    [Fact]
    public async Task Permissao_vale_so_para_a_sala_do_canal_de_voz()
    {
        var (dinoId, dino) = await PlusAsync("dino");
        var (_, outsider) = await PlusAsync("estranho");
        var group = (await (await dino.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("G"))).Content.ReadFromJsonAsync<GroupDetails>())!;
        var voice = group.Channels.First(c => c.Kind == ChannelKind.Voice);
        var text = group.Channels.First(c => c.Kind == ChannelKind.Text);

        var join = (await (await dino.PostAsync(SocialRoutes.ChannelVoice(voice.Id), null)).Content.ReadFromJsonAsync<VoiceJoin>())!;
        var onText = await dino.PostAsync(SocialRoutes.ChannelVoice(text.Id), null);
        var fromOutsider = await outsider.PostAsync(SocialRoutes.ChannelVoice(voice.Id), null);

        Assert.Equal("wss://midia.exemplo.test", join.Url);
        Assert.Equal(VoiceService.RoomName(voice.Id), join.Room);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(join.Token, new TokenValidationParameters
        {
            ValidIssuer = Key,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
            LifetimeValidator = (_, _, _, _) => true,
        });
        Assert.True(validation.IsValid);
        Assert.Equal(dinoId.ToString("N"), validation.Claims["sub"]);
        var grant = JsonDocument.Parse(JsonSerializer.Serialize(validation.Claims["video"])).RootElement;
        Assert.Equal(join.Room, grant.GetProperty("room").GetString());
        Assert.True(grant.GetProperty("roomJoin").GetBoolean());
        Assert.Contains("pt-BR", (string)validation.Claims["metadata"]);
        Assert.Equal(HttpStatusCode.BadRequest, onText.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, fromOutsider.StatusCode);
    }

    [Fact]
    public async Task Webhook_assinado_atualiza_quem_esta_na_sala_e_o_falso_e_recusado()
    {
        var (dinoId, dino) = await PlusAsync("dino");
        var group = (await (await dino.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("G"))).Content.ReadFromJsonAsync<GroupDetails>())!;
        var voice = group.Channels.First(c => c.Kind == ChannelKind.Voice);
        using var anonymous = _factory.CreateClient();

        var body = JsonSerializer.Serialize(new
        {
            @event = "participant_joined",
            room = new { name = VoiceService.RoomName(voice.Id) },
            participant = new { identity = dinoId.ToString("N") },
        });
        var accepted = await anonymous.SendAsync(Webhook(body, Secret));
        var members = await (await dino.GetAsync(SocialRoutes.ChannelVoice(voice.Id))).Content.ReadFromJsonAsync<List<Guid>>();

        var forged = await anonymous.SendAsync(Webhook(body.Replace("joined", "left"), "segredo-errado-com-mais-de-32-caracteres!"));
        var tampered = Webhook(body, Secret);
        tampered.Content = new StringContent(body.Replace("joined", "left"), Encoding.UTF8, "application/webhook+json");
        var changedBody = await anonymous.SendAsync(tampered);
        var after = await (await dino.GetAsync(SocialRoutes.ChannelVoice(voice.Id))).Content.ReadFromJsonAsync<List<Guid>>();

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal([dinoId], members);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, changedBody.StatusCode);
        Assert.Equal([dinoId], after);
    }

    [Fact]
    public async Task Sem_LiveKit_configurado_avisa()
    {
        using var plain = new ServerFactory();
        await plain.CreateUserAsync("dino");
        await using (var scope = plain.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPlanAsync("dino", AccountPlan.Plus, null);
        }
        using var http = plain.CreateClient();
        var tokens = await plain.LoginAsync(http, "dino");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var group = (await (await http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("G"))).Content.ReadFromJsonAsync<GroupDetails>())!;

        var response = await http.PostAsync(SocialRoutes.ChannelVoice(group.Channels.First(c => c.Kind == ChannelKind.Voice).Id), null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Como o LiveKit assina: JWT HS256 com o SHA-256 (base64) do corpo.</summary>
    private static HttpRequestMessage Webhook(string body, string secret)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Key,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sha256"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))) },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
        });
        var request = new HttpRequestMessage(HttpMethod.Post, SocialRoutes.LiveKitWebhook)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/webhook+json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", token);
        return request;
    }
}
