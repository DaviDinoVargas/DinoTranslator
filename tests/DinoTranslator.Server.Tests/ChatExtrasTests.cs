using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DinoTranslator.Server.Auth;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Social;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

/// <summary>Reações, respostas com citação, menções, fixadas, busca e o plano Pro.</summary>
public sealed class ChatExtrasTests : IDisposable
{
    private readonly ServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<(Guid Id, HttpClient Http)> PersonAsync(string userName, AccountPlan plan = AccountPlan.Plus)
    {
        var profile = await _factory.CreateUserAsync(userName);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPlanAsync(userName, plan, null);
        }
        var http = _factory.CreateClient();
        var tokens = await _factory.LoginAsync(http, userName);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (profile.Id, http);
    }

    private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(Guid Text, (Guid Id, HttpClient Http) Owner, (Guid Id, HttpClient Http) Member)> GroupAsync()
    {
        var owner = await PersonAsync("dino", AccountPlan.Pro);
        var member = await PersonAsync("anna");
        var group = await OkAsync<GroupDetails>(owner.Http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("Família")));
        var invite = await OkAsync<InviteInfo>(owner.Http.PostAsJsonAsync(SocialRoutes.GroupInvites(group.Group.Id), new CreateInviteRequest()));
        await OkAsync<GroupDetails>(member.Http.PostAsync(SocialRoutes.InviteJoin(invite.Code), null));
        return (group.Channels.First(c => c.Kind == ChannelKind.Text).Id, owner, member);
    }

    [Fact]
    public async Task Pro_tambem_usa_a_versao_paga()
    {
        var pro = await PersonAsync("pro", AccountPlan.Pro);

        Assert.Equal(HttpStatusCode.OK, (await pro.Http.GetAsync(SocialRoutes.MyGroups)).StatusCode);
    }

    [Fact]
    public async Task Reacoes_so_da_lista_e_cada_pessoa_uma_vez()
    {
        var (text, owner, member) = await GroupAsync();
        var message = await OkAsync<MessageInfo>(owner.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text), new SendMessageRequest("Oi")));

        await OkAsync<List<ReactionInfo>>(member.Http.PutAsync(SocialRoutes.MessageReaction(message.Id, "👍"), null));
        await OkAsync<List<ReactionInfo>>(member.Http.PutAsync(SocialRoutes.MessageReaction(message.Id, "👍"), null));
        var both = await OkAsync<List<ReactionInfo>>(owner.Http.PutAsync(SocialRoutes.MessageReaction(message.Id, "👍"), null));
        var invalid = await member.Http.PutAsync(SocialRoutes.MessageReaction(message.Id, "<b>"), null);
        var removed = await OkAsync<List<ReactionInfo>>(member.Http.DeleteAsync(SocialRoutes.MessageReaction(message.Id, "👍")));

        Assert.Equal(2, Assert.Single(both).Users.Count);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal([owner.Id], Assert.Single(removed).Users);
    }

    [Fact]
    public async Task Resposta_traz_a_citacao_e_mencao_so_de_quem_ve_o_canal()
    {
        var (text, owner, member) = await GroupAsync();
        await PersonAsync("estranho");
        var first = await OkAsync<MessageInfo>(member.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text), new SendMessageRequest("Vamos jogar hoje?")));

        var reply = await OkAsync<MessageInfo>(owner.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text),
            new SendMessageRequest("Bora, @anna! @estranho não está no grupo. e-mail@anna.com não é menção.", first.Id)));

        Assert.Equal(first.Id, reply.Reply!.Id);
        Assert.Equal("anna", reply.Reply.AuthorName);
        Assert.Equal("Vamos jogar hoje?", reply.Reply.Content);
        Assert.Equal([member.Id], reply.Mentions);
    }

    [Fact]
    public async Task So_administrador_fixa_no_grupo_e_a_lista_aparece()
    {
        var (text, owner, member) = await GroupAsync();
        var message = await OkAsync<MessageInfo>(member.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text), new SendMessageRequest("Regras do grupo")));

        var byMember = await member.Http.PutAsync(SocialRoutes.MessagePin(message.Id), null);
        var pinned = await OkAsync<MessageInfo>(owner.Http.PutAsync(SocialRoutes.MessagePin(message.Id), null));
        var pins = await OkAsync<List<MessageInfo>>(member.Http.GetAsync(SocialRoutes.ChannelPins(text)));
        var unpinned = await OkAsync<MessageInfo>(owner.Http.DeleteAsync(SocialRoutes.MessagePin(message.Id)));

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.True(pinned.Pinned);
        Assert.Equal(message.Id, Assert.Single(pins).Id);
        Assert.False(unpinned.Pinned);
    }

    [Fact]
    public async Task Busca_acha_so_no_que_a_pessoa_pode_ler()
    {
        var (text, owner, member) = await GroupAsync();
        var outsider = await PersonAsync("estranho");
        await OkAsync<MessageInfo>(owner.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text), new SendMessageRequest("A senha do wifi é 100%_segura")));
        await OkAsync<MessageInfo>(owner.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text), new SendMessageRequest("Outra coisa")));
        var apagada = await OkAsync<MessageInfo>(owner.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text), new SendMessageRequest("WIFI antigo")));
        await owner.Http.DeleteAsync(SocialRoutes.Message(apagada.Id));

        var found = await OkAsync<List<SearchResult>>(member.Http.GetAsync(SocialRoutes.Search + "?q=wifi"));
        var wildcard = await OkAsync<List<SearchResult>>(member.Http.GetAsync(SocialRoutes.Search + "?q=" + Uri.EscapeDataString("100%_")));
        var notMine = await OkAsync<List<SearchResult>>(outsider.Http.GetAsync(SocialRoutes.Search + "?q=wifi"));
        var tooShort = await member.Http.GetAsync(SocialRoutes.Search + "?q=a");

        var result = Assert.Single(found);
        Assert.Equal("#geral", result.ChannelName);
        Assert.Contains("wifi", result.Message.Content);
        Assert.Single(wildcard);
        Assert.Empty(notMine);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
    }
}
