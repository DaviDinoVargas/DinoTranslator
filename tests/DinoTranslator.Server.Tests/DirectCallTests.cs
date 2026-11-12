using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using DinoTranslator.Server.Auth;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Server.Tests;

/// <summary>Chamada 1:1 pela conta: o servidor só repassa convite e resposta entre as duas pessoas da conversa.</summary>
public sealed class DirectCallTests : IAsyncLifetime
{
    private const string CallId = "0123456789abcdef0123456789abcdef";

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

    private sealed record Live(HubConnection Connection, Channel<CallSignalInfo> Signals);

    private async Task<Person> PersonAsync(string userName)
    {
        var profile = await _factory.CreateUserAsync(userName);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPlanAsync(userName, AccountPlan.Plus, null);
        }
        var http = _factory.CreateClient();
        var tokens = await _factory.LoginAsync(http, userName);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new Person(profile.Id, tokens.AccessToken, http);
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
        var live = new Live(connection, Channel.CreateUnbounded<CallSignalInfo>());
        connection.On<CallSignalInfo>(nameof(IAppClient.DirectCall), s => live.Signals.Writer.TryWrite(s));
        await connection.StartAsync();
        Assert.True(await connection.InvokeAsync<bool>(AppHubMethods.Ready));
        _connections.Add(connection);
        return live;
    }

    /// <summary>Dino e Anna amigos, com a conversa direta aberta.</summary>
    private async Task<(Person Dino, Person Anna, Guid Channel)> FriendsAsync()
    {
        var dino = await PersonAsync("dino");
        var anna = await PersonAsync("anna");
        Assert.True((await anna.Http.PostAsJsonAsync(SocialRoutes.Friends, new FriendRequest("dino"))).IsSuccessStatusCode);
        Assert.True((await dino.Http.PostAsJsonAsync(SocialRoutes.Friends, new FriendRequest("anna"))).IsSuccessStatusCode);
        var direct = await (await dino.Http.PostAsync(SocialRoutes.Direct(anna.Id), null)).Content.ReadFromJsonAsync<DirectInfo>();
        return (dino, anna, direct!.Channel.Id);
    }

    private static Task<bool> SignalAsync(Live live, Guid channel, CallSignal signal, string? code = null, string callId = CallId) =>
        live.Connection.InvokeAsync<bool>(AppHubMethods.DirectCall, channel, callId, signal, code);

    private static async Task<CallSignalInfo> NextAsync(Live live)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await live.Signals.Reader.ReadAsync(timeout.Token);
    }

    private static async Task<bool> NothingAsync(Live live)
    {
        await Task.Delay(700);
        return !live.Signals.Reader.TryRead(out _);
    }

    [Fact]
    public async Task Convite_e_resposta_passam_so_entre_os_dois_e_o_outro_aparelho_para_de_tocar()
    {
        var (dino, anna, channel) = await FriendsAsync();
        var dinoLive = await ConnectAsync(dino);
        var annaDesktop = await ConnectAsync(anna);
        var annaNotebook = await ConnectAsync(anna);

        var online = await SignalAsync(dinoLive, channel, CallSignal.Ring, "convite");
        var ringDesktop = await NextAsync(annaDesktop);
        var ringNotebook = await NextAsync(annaNotebook);
        await SignalAsync(annaDesktop, channel, CallSignal.Answer, "resposta");
        var answer = await NextAsync(dinoLive);
        var handled = await NextAsync(annaNotebook);

        Assert.True(online);
        Assert.Equal(new CallSignalInfo(channel, dino.Id, CallId, CallSignal.Ring, "convite"), ringDesktop);
        Assert.Equal(ringDesktop, ringNotebook);
        Assert.Equal(new CallSignalInfo(channel, anna.Id, CallId, CallSignal.Answer, "resposta"), answer);
        Assert.Equal(new CallSignalInfo(channel, dino.Id, CallId, CallSignal.Handled, null), handled);
        Assert.True(await NothingAsync(dinoLive));
    }

    [Fact]
    public async Task Avisa_quando_a_outra_pessoa_nao_esta_conectada()
    {
        var (dino, _, channel) = await FriendsAsync();
        var dinoLive = await ConnectAsync(dino);

        Assert.False(await SignalAsync(dinoLive, channel, CallSignal.Ring, "convite"));
    }

    [Fact]
    public async Task Quem_nao_e_da_conversa_ou_esta_bloqueado_nao_liga()
    {
        var (dino, anna, channel) = await FriendsAsync();
        var outsider = await ConnectAsync(await PersonAsync("estranho"));
        var annaLive = await ConnectAsync(anna);
        var dinoLive = await ConnectAsync(dino);

        await Assert.ThrowsAsync<HubException>(() => SignalAsync(outsider, channel, CallSignal.Ring, "convite"));
        Assert.True(await NothingAsync(annaLive));

        Assert.True((await anna.Http.PostAsync(SocialRoutes.Block(dino.Id), null)).IsSuccessStatusCode);
        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Ring, "convite"));
        Assert.True(await NothingAsync(annaLive));
    }

    [Fact]
    public async Task Sinal_mal_formado_e_recusado()
    {
        var (dino, _, channel) = await FriendsAsync();
        var dinoLive = await ConnectAsync(dino);

        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Ring, null));
        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Ring, new string('x', CallSignalLimits.MaxCodeLength + 1)));
        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Cancel, "não leva código"));
        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Handled));
        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, (CallSignal)42));
        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Ring, "convite", "../../outra"));
        await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Ring, "convite", CallId.ToUpperInvariant()));
    }

    [Fact]
    public async Task Toques_em_excesso_esperam()
    {
        var (dino, anna, channel) = await FriendsAsync();
        var dinoLive = await ConnectAsync(dino);
        await ConnectAsync(anna);

        for (var i = 0; i < CallSignalLimits.RingBurst; i++)
        {
            await SignalAsync(dinoLive, channel, CallSignal.Ring, "convite");
        }
        var tooMany = await Assert.ThrowsAsync<HubException>(() => SignalAsync(dinoLive, channel, CallSignal.Ring, "convite"));

        Assert.Contains("Aguarde", tooMany.Message);
        // Desistir não conta como toque.
        await SignalAsync(dinoLive, channel, CallSignal.Cancel);
    }
}
