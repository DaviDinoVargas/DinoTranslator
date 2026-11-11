using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using DinoTranslator.Server.Auth;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

/// <summary>Versão paga: grupos, convites, papéis, mensagens, amigos, bloqueios e tempo real.</summary>
public sealed class SocialTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();
    private readonly List<IAsyncDisposable> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
        _factory.Dispose();
    }

    private sealed record Person(Guid Id, string Token, HttpClient Http);

    private async Task<Person> PersonAsync(string userName, bool plus = true, string language = Languages.PortugueseBrazil)
    {
        var profile = await _factory.CreateUserAsync(userName, language: language);
        if (plus)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPlanAsync(userName, AccountPlan.Plus, null);
        }
        var http = _factory.CreateClient();
        var tokens = await _factory.LoginAsync(http, userName);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new Person(profile.Id, tokens.AccessToken, http);
    }

    /// <summary>Conexão em tempo real que guarda os eventos recebidos.</summary>
    private sealed class Live(HubConnection connection)
    {
        public HubConnection Connection { get; } = connection;

        public Channel<MessageInfo> Messages { get; } = Channel.CreateUnbounded<MessageInfo>();

        public Channel<Guid> Removed { get; } = Channel.CreateUnbounded<Guid>();

        public Channel<(Guid User, bool Online)> Presence { get; } = Channel.CreateUnbounded<(Guid, bool)>();

        public Channel<DirectInfo> Directs { get; } = Channel.CreateUnbounded<DirectInfo>();
    }

    private async Task<Live> ConnectAsync(Person person)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, SocialRoutes.Hub), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Headers["Authorization"] = "Bearer " + person.Token;
            })
            .Build();
        var live = new Live(connection);
        connection.On<MessageInfo>(nameof(IAppClient.MessageCreated), m => live.Messages.Writer.TryWrite(m));
        connection.On<Guid>(nameof(IAppClient.GroupRemoved), g => live.Removed.Writer.TryWrite(g));
        connection.On<Guid, bool>(nameof(IAppClient.PresenceChanged), (u, o) => live.Presence.Writer.TryWrite((u, o)));
        connection.On<DirectInfo>(nameof(IAppClient.DirectOpened), d => live.Directs.Writer.TryWrite(d));
        await connection.StartAsync();
        _connections.Add(connection);
        return live;
    }

    private static async Task<T> NextAsync<T>(Channel<T> channel, int seconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    private static async Task<bool> NothingAsync<T>(Channel<T> channel, int milliseconds = 700)
    {
        await Task.Delay(milliseconds);
        return !channel.Reader.TryRead(out _);
    }

    private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<GroupDetails> GroupWithMemberAsync(Person owner, Person member, string name = "Amigos")
    {
        var group = await OkAsync<GroupDetails>(owner.Http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest(name)));
        var invite = await OkAsync<InviteInfo>(owner.Http.PostAsJsonAsync(SocialRoutes.GroupInvites(group.Group.Id), new CreateInviteRequest()));
        return await OkAsync<GroupDetails>(member.Http.PostAsync(SocialRoutes.InviteJoin(invite.Code), null));
    }

    [Fact]
    public async Task Conta_gratis_nao_usa_a_versao_paga()
    {
        var free = await PersonAsync("gratis", plus: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await free.Http.GetAsync(SocialRoutes.MyGroups)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await free.Http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("x"))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(free));
    }

    [Fact]
    public async Task Grupo_nasce_com_canais_e_so_membros_veem()
    {
        var dino = await PersonAsync("dino");
        var outsider = await PersonAsync("estranho");

        var group = await OkAsync<GroupDetails>(dino.Http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("  Família  ")));

        Assert.Equal("Família", group.Group.Name);
        Assert.Equal(GroupRole.Owner, group.Group.MyRole);
        Assert.Contains(group.Channels, c => c.Kind == ChannelKind.Text);
        Assert.Contains(group.Channels, c => c.Kind == ChannelKind.Voice);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync(SocialRoutes.Group(group.Group.Id))).StatusCode);
        var text = group.Channels.First(c => c.Kind == ChannelKind.Text);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync(SocialRoutes.ChannelMessages(text.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest("oi"))).StatusCode);
    }

    [Fact]
    public async Task Convite_respeita_o_limite_de_usos_e_a_validade()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        var third = await PersonAsync("terceira");
        var group = await OkAsync<GroupDetails>(dino.Http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("G")));

        var single = await OkAsync<InviteInfo>(dino.Http.PostAsJsonAsync(SocialRoutes.GroupInvites(group.Group.Id), new CreateInviteRequest(MaxUses: 1)));
        var joined = await OkAsync<GroupDetails>(anna.Http.PostAsync(SocialRoutes.InviteJoin(single.Code), null));
        var refused = await third.Http.PostAsync(SocialRoutes.InviteJoin(single.Code), null);

        Assert.Equal(2, joined.Group.Members);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var shortLived = await OkAsync<InviteInfo>(dino.Http.PostAsJsonAsync(SocialRoutes.GroupInvites(group.Group.Id), new CreateInviteRequest(Hours: 1)));
        _factory.Time.Advance(TimeSpan.FromHours(2));
        // O token de acesso também venceu nessas duas horas: entra de novo.
        var fresh = await _factory.LoginAsync(third.Http, "terceira");
        third.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fresh.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await third.Http.GetAsync(SocialRoutes.Invite(shortLived.Code))).StatusCode);
    }

    [Fact]
    public async Task Mensagem_chega_em_tempo_real_com_o_idioma_de_quem_escreveu()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna", language: Languages.Russian);
        var group = await GroupWithMemberAsync(dino, anna);
        var text = group.Channels.First(c => c.Kind == ChannelKind.Text);
        var annaLive = await ConnectAsync(anna);

        var sent = await OkAsync<MessageInfo>(dino.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest("Oi, Anna!")));
        var received = await NextAsync(annaLive.Messages);
        var reply = await OkAsync<MessageInfo>(anna.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest("Привет!", sent.Id)));
        var page = await OkAsync<MessagePage>(dino.Http.GetAsync(SocialRoutes.ChannelMessages(text.Id) + "?limite=1"));

        Assert.Equal(sent.Id, received.Id);
        Assert.Equal("Oi, Anna!", received.Content);
        Assert.Equal(Languages.PortugueseBrazil, received.Language);
        Assert.Equal(Languages.Russian, reply.Language);
        Assert.Equal(sent.Id, reply.ReplyToId);
        Assert.Equal(reply.Id, Assert.Single(page.Messages).Id);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task So_quem_escreveu_edita_e_admin_apaga()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        var group = await GroupWithMemberAsync(dino, anna);
        var text = group.Channels.First(c => c.Kind == ChannelKind.Text);
        var message = await OkAsync<MessageInfo>(anna.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest("errado")));

        var editByOther = await dino.Http.PatchAsJsonAsync(SocialRoutes.Message(message.Id), new EditMessageRequest("hack"));
        var edited = await OkAsync<MessageInfo>(anna.Http.PatchAsJsonAsync(SocialRoutes.Message(message.Id), new EditMessageRequest("certo")));
        var deleted = await dino.Http.DeleteAsync(SocialRoutes.Message(message.Id));
        var page = await OkAsync<MessagePage>(anna.Http.GetAsync(SocialRoutes.ChannelMessages(text.Id)));

        Assert.Equal(HttpStatusCode.Forbidden, editByOther.StatusCode);
        Assert.Equal("certo", edited.Content);
        Assert.NotNull(edited.EditedAt);
        Assert.True(deleted.IsSuccessStatusCode);
        var gone = Assert.Single(page.Messages);
        Assert.True(gone.Deleted);
        Assert.Empty(gone.Content);
    }

    [Fact]
    public async Task Removido_do_grupo_para_de_receber_na_hora()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        var group = await GroupWithMemberAsync(dino, anna);
        var text = group.Channels.First(c => c.Kind == ChannelKind.Text);
        var annaLive = await ConnectAsync(anna);

        var kicked = await dino.Http.DeleteAsync(SocialRoutes.GroupMember(group.Group.Id, anna.Id));
        var removed = await NextAsync(annaLive.Removed);
        await dino.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest("segredo"));

        Assert.True(kicked.IsSuccessStatusCode);
        Assert.Equal(group.Group.Id, removed);
        Assert.True(await NothingAsync(annaLive.Messages));
        Assert.Equal(HttpStatusCode.NotFound, (await anna.Http.GetAsync(SocialRoutes.ChannelMessages(text.Id))).StatusCode);
    }

    [Fact]
    public async Task Papeis_membro_nao_remove_admin_e_dono_nao_sai()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        var third = await PersonAsync("terceira");
        var group = await GroupWithMemberAsync(dino, anna);
        var invite = await OkAsync<InviteInfo>(dino.Http.PostAsJsonAsync(SocialRoutes.GroupInvites(group.Group.Id), new CreateInviteRequest()));
        await OkAsync<GroupDetails>(third.Http.PostAsync(SocialRoutes.InviteJoin(invite.Code), null));

        var promote = await dino.Http.PutAsJsonAsync(SocialRoutes.GroupMemberRole(group.Group.Id, anna.Id), new RoleRequest(GroupRole.Admin));
        var memberKicksAdmin = await third.Http.DeleteAsync(SocialRoutes.GroupMember(group.Group.Id, anna.Id));
        var adminKicksOwner = await anna.Http.DeleteAsync(SocialRoutes.GroupMember(group.Group.Id, dino.Id));
        var ownerLeaves = await dino.Http.DeleteAsync(SocialRoutes.GroupMember(group.Group.Id, dino.Id));
        var memberLeaves = await third.Http.DeleteAsync(SocialRoutes.GroupMember(group.Group.Id, third.Id));

        Assert.True(promote.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberKicksAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adminKicksOwner.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, ownerLeaves.StatusCode);
        Assert.True(memberLeaves.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Amizade_conversa_direta_e_bloqueio()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        var dinoLive = await ConnectAsync(dino);

        // Sem amizade nem grupo em comum, não há conversa direta.
        Assert.Equal(HttpStatusCode.Forbidden, (await dino.Http.PostAsync(SocialRoutes.Direct(anna.Id), null)).StatusCode);

        var request = await OkAsync<FriendInfo>(anna.Http.PostAsJsonAsync(SocialRoutes.Friends, new FriendRequest("Dino")));
        var pending = await OkAsync<List<FriendInfo>>(dino.Http.GetAsync(SocialRoutes.Friends));
        var accepted = await OkAsync<FriendInfo>(dino.Http.PostAsJsonAsync(SocialRoutes.Friends, new FriendRequest("anna")));
        var direct = await OkAsync<DirectInfo>(anna.Http.PostAsync(SocialRoutes.Direct(dino.Id), null));
        var opened = await NextAsync(dinoLive.Directs);
        await OkAsync<MessageInfo>(anna.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(direct.Channel.Id), new SendMessageRequest("oi")));
        var received = await NextAsync(dinoLive.Messages);

        Assert.Equal(FriendState.Outgoing, request.State);
        Assert.Equal(FriendState.Incoming, Assert.Single(pending).State);
        Assert.Equal(FriendState.Friends, accepted.State);
        Assert.Equal(direct.Channel.Id, opened.Channel.Id);
        Assert.Equal("oi", received.Content);

        Assert.True((await dino.Http.PostAsync(SocialRoutes.Block(anna.Id), null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(direct.Channel.Id), new SendMessageRequest("oi?"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.Http.PostAsJsonAsync(SocialRoutes.Friends, new FriendRequest("dino"))).StatusCode);
        Assert.Empty(await OkAsync<List<FriendInfo>>(dino.Http.GetAsync(SocialRoutes.Friends)));
    }

    [Fact]
    public async Task Presenca_avisa_os_amigos()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        await OkAsync<FriendInfo>(anna.Http.PostAsJsonAsync(SocialRoutes.Friends, new FriendRequest("dino")));
        await OkAsync<FriendInfo>(dino.Http.PostAsJsonAsync(SocialRoutes.Friends, new FriendRequest("anna")));
        var dinoLive = await ConnectAsync(dino);

        var annaLive = await ConnectAsync(anna);
        var online = await NextAsync(dinoLive.Presence);
        var friends = await OkAsync<List<FriendInfo>>(dino.Http.GetAsync(SocialRoutes.Friends));
        await annaLive.Connection.StopAsync();
        var offline = await NextAsync(dinoLive.Presence);

        Assert.Equal((anna.Id, true), online);
        Assert.True(Assert.Single(friends).Online);
        Assert.Equal((anna.Id, false), offline);
    }

    [Fact]
    public async Task Limite_de_mensagens_e_por_conta_e_nao_por_ip()
    {
        // As duas contas chegam do mesmo IP (como numa casa ou atrás do CGNAT da operadora).
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        var group = await GroupWithMemberAsync(dino, anna);
        var text = group.Channels.First(c => c.Kind == ChannelKind.Text);

        var statuses = new List<HttpStatusCode>();
        foreach (var person in new[] { dino, anna })
        {
            for (var i = 0; i < 10; i++)
            {
                statuses.Add((await person.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest($"m{i}"))).StatusCode);
            }
        }

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    [Fact]
    public async Task Mensagem_vazia_ou_grande_demais_e_recusada_e_ha_limite_por_conta()
    {
        var dino = await PersonAsync("dino");
        var group = await OkAsync<GroupDetails>(dino.Http.PostAsJsonAsync(SocialRoutes.Groups, new NameRequest("G")));
        var text = group.Channels.First(c => c.Kind == ChannelKind.Text);
        var voice = group.Channels.First(c => c.Kind == ChannelKind.Voice);

        Assert.Equal(HttpStatusCode.BadRequest, (await dino.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest("   "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await dino.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest(new string('a', SocialLimits.MaxMessageLength + 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await dino.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(voice.Id), new SendMessageRequest("oi"))).StatusCode);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await dino.Http.PostAsJsonAsync(SocialRoutes.ChannelMessages(text.Id), new SendMessageRequest($"m{i}"))).StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
