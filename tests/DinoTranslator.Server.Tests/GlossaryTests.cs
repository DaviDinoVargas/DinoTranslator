using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DinoTranslator.Server.Auth;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Social;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

/// <summary>Glossário pessoal (da conta) e do grupo (só administradores mudam).</summary>
public sealed class GlossaryTests : IDisposable
{
    private readonly ServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> PersonAsync(string userName)
    {
        await _factory.CreateUserAsync(userName);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPlanAsync(userName, AccountPlan.Plus, null);
        }
        var http = _factory.CreateClient();
        var tokens = await _factory.LoginAsync(http, userName);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return http;
    }

    private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Pessoal_e_de_cada_conta_e_vale_em_qualquer_aparelho()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");

        var saved = await OkAsync<List<GlossaryEntry>>(dino.PutAsJsonAsync(SocialRoutes.Glossary, new GlossaryRequest(
        [
            new GlossaryEntry("  Rosa  "),
            new GlossaryEntry("Pão de Açúcar", "Сахарная Голова", Languages.Russian),
            new GlossaryEntry("deploy", "", Languages.Russian),
        ])));
        var again = await OkAsync<List<GlossaryEntry>>(dino.GetAsync(SocialRoutes.Glossary));
        var annas = await OkAsync<List<GlossaryEntry>>(anna.GetAsync(SocialRoutes.Glossary));

        Assert.Equal(
        [
            new GlossaryEntry("Rosa"),
            new GlossaryEntry("Pão de Açúcar", "Сахарная Голова", Languages.Russian),
            new GlossaryEntry("deploy"),
        ], saved);
        Assert.Equal(saved, again);
        Assert.Empty(annas);
    }

    [Fact]
    public async Task Termo_invalido_ou_repetido_e_recusado()
    {
        var dino = await PersonAsync("dino");

        var repeated = await dino.PutAsJsonAsync(SocialRoutes.Glossary, new GlossaryRequest([new GlossaryEntry("Rosa"), new GlossaryEntry("rosa")]));
        var tooLong = await dino.PutAsJsonAsync(SocialRoutes.Glossary, new GlossaryRequest([new GlossaryEntry(new string('a', 61))]));
        var noLanguage = await dino.PutAsJsonAsync(SocialRoutes.Glossary, new GlossaryRequest([new GlossaryEntry("Rosa", "Роза", "xx-XX")]));
        var tooMany = await dino.PutAsJsonAsync(SocialRoutes.Glossary, new GlossaryRequest(
            [.. Enumerable.Range(0, GlossaryLimits.MaxPersonal + 1).Select(i => new GlossaryEntry($"termo {i}"))]));
        var empty = await dino.PutAsJsonAsync(SocialRoutes.Glossary, new GlossaryRequest([new GlossaryEntry("   ")]));

        Assert.All(new[] { repeated, tooLong, noLanguage, tooMany, empty }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Contains("200", (await tooMany.Content.ReadAsStringAsync()));
        Assert.Empty(await OkAsync<List<GlossaryEntry>>(dino.GetAsync(SocialRoutes.Glossary)));
    }

    [Fact]
    public async Task Do_grupo_todos_leem_e_so_administrador_muda()
    {
        var owner = await PersonAsync("dino");
        var member = await PersonAsync("anna");
        var outsider = await PersonAsync("estranho");
        var group = await OkAsync<GroupDetails>(owner.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("Time")));
        var invite = await OkAsync<InviteInfo>(owner.PostAsJsonAsync(SocialRoutes.GroupInvites(group.Group.Id), new CreateInviteRequest()));
        await OkAsync<GroupDetails>(member.PostAsync(SocialRoutes.InviteJoin(invite.Code), null));

        var saved = await OkAsync<List<GlossaryEntry>>(owner.PutAsJsonAsync(SocialRoutes.GroupGlossary(group.Group.Id),
            new GlossaryRequest([new GlossaryEntry("Sprint Review"), new GlossaryEntry("Kanban", "Канбан", Languages.Russian)])));
        var read = await OkAsync<List<GlossaryEntry>>(member.GetAsync(SocialRoutes.GroupGlossary(group.Group.Id)));
        var byMember = await member.PutAsJsonAsync(SocialRoutes.GroupGlossary(group.Group.Id), new GlossaryRequest([]));
        var byOutsider = await outsider.GetAsync(SocialRoutes.GroupGlossary(group.Group.Id));

        Assert.Equal(saved, read);
        Assert.Equal(2, read.Count);
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
    }
}
