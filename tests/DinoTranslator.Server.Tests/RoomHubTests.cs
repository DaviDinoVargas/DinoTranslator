using System.Net;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Auth;
using DinoTranslator.Shared.Room;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace DinoTranslator.Server.Tests;

public sealed class RoomHubTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ServerFactory _factory = new();
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
        _factory.Dispose();
    }

    [Fact]
    public async Task Conexao_sem_token_e_recusada()
    {
        var connection = Connect(accessToken: null);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task Token_na_query_string_nao_e_aceito()
    {
        var (_, token) = await CreateUserAndLoginAsync("dino");
        var socketClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(_factory.Server.BaseAddress, $"{RoomRoutes.Hub}?access_token={token}");

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            socketClient.ConnectAsync(new UriBuilder(uri) { Scheme = "ws" }.Uri, CancellationToken.None));

        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task WebSocket_com_token_no_cabecalho_conecta()
    {
        var (_, token) = await CreateUserAndLoginAsync("dino");
        var connection = Connect(token, HttpTransportType.WebSockets);

        await connection.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Outro_usuario_e_avisado_quando_alguem_entra_e_sai()
    {
        var (dino, dinoToken) = await CreateUserAndLoginAsync("dino");
        var (_, amigaToken) = await CreateUserAndLoginAsync("amiga", Languages.Russian);

        var amiga = Connect(amigaToken);
        var presence = Collect<Participant>(amiga, nameof(IRoomClient.PresenceChanged));
        await amiga.StartAsync();

        var dinoConnection = Connect(dinoToken);
        await dinoConnection.StartAsync();
        var online = await presence.ReadAsync();
        Assert.Equal(dino, online.Profile);
        Assert.True(online.Online);

        await dinoConnection.StopAsync();
        var offline = await presence.ReadAsync();
        Assert.False(offline.Online);
    }

    [Fact]
    public async Task Lista_os_dois_participantes_com_status()
    {
        var (dino, dinoToken) = await CreateUserAndLoginAsync("dino");
        var (amiga, _) = await CreateUserAndLoginAsync("amiga", Languages.Russian);
        var connection = Connect(dinoToken);
        await connection.StartAsync();

        var participants = await connection.InvokeAsync<List<Participant>>(RoomHubMethods.GetParticipants);

        Assert.Equal([new Participant(dino, true), new Participant(amiga, false)], participants);
    }

    [Fact]
    public async Task Sinal_chega_so_ao_outro_usuario_com_o_remetente_certo()
    {
        var (dino, dinoToken) = await CreateUserAndLoginAsync("dino");
        var (_, amigaToken) = await CreateUserAndLoginAsync("amiga", Languages.Russian);
        var dinoConnection = Connect(dinoToken);
        var amigaConnection = Connect(amigaToken);
        var received = Collect<ReceivedSignal>(amigaConnection, nameof(IRoomClient.SignalReceived));
        var echo = Collect<ReceivedSignal>(dinoConnection, nameof(IRoomClient.SignalReceived));
        await dinoConnection.StartAsync();
        await amigaConnection.StartAsync();

        await dinoConnection.InvokeAsync(RoomHubMethods.SendSignal, new SignalMessage(SignalTypes.Offer, "v=0 sdp"));

        Assert.Equal(new ReceivedSignal(dino.Id, SignalTypes.Offer, "v=0 sdp"), await received.ReadAsync());
        Assert.False(echo.HasAny);
    }

    [Theory]
    [InlineData("script")]
    [InlineData("")]
    public async Task Tipo_de_sinal_desconhecido_e_recusado(string type)
    {
        var (dinoConnection, _) = await ConnectBothAsync();

        var error = await Assert.ThrowsAsync<HubException>(() =>
            dinoConnection.InvokeAsync(RoomHubMethods.SendSignal, new SignalMessage(type, null)));

        Assert.Contains("Tipo de sinal inválido", error.Message);
    }

    [Fact]
    public async Task Sinal_grande_demais_e_recusado()
    {
        var (dinoConnection, _) = await ConnectBothAsync();
        var payload = new string('a', SignalTypes.MaxPayloadLength + 1);

        var error = await Assert.ThrowsAsync<HubException>(() =>
            dinoConnection.InvokeAsync(RoomHubMethods.SendSignal, new SignalMessage(SignalTypes.Offer, payload)));

        Assert.Contains("grande demais", error.Message);
    }

    [Fact]
    public async Task Sinal_para_usuario_offline_e_recusado()
    {
        var (_, dinoToken) = await CreateUserAndLoginAsync("dino");
        await CreateUserAndLoginAsync("amiga", Languages.Russian);
        var connection = Connect(dinoToken);
        await connection.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync(RoomHubMethods.SendSignal, new SignalMessage(SignalTypes.Invite, null)));

        Assert.Contains("offline", error.Message);
    }

    [Fact]
    public async Task Nova_conexao_da_mesma_conta_substitui_a_anterior()
    {
        var (_, token) = await CreateUserAndLoginAsync("dino");
        var first = Connect(token);
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.On(nameof(IRoomClient.SessionReplaced), () => replaced.TrySetResult());
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await first.StartAsync();

        await Connect(token).StartAsync();

        await replaced.Task.WaitAsync(Timeout);
        await closed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Rajada_de_mensagens_acima_do_limite_e_recusada()
    {
        var (dinoConnection, _) = await ConnectBothAsync();
        var candidate = new SignalMessage(SignalTypes.Candidate, "candidate:1 1 udp 1 10.0.0.1 9 typ host");

        // Bem acima do balde (200 + 50 por segundo): mesmo numa máquina lenta, a rajada estoura o limite.
        var results = await Task.WhenAll(Enumerable.Range(0, 1_500).Select(async _ =>
        {
            try
            {
                await dinoConnection.InvokeAsync(RoomHubMethods.SendSignal, candidate);
                return true;
            }
            catch (HubException)
            {
                return false;
            }
        }));

        Assert.Contains(false, results);
    }

    private async Task<(HubConnection Dino, HubConnection Amiga)> ConnectBothAsync()
    {
        var (_, dinoToken) = await CreateUserAndLoginAsync("dino");
        var (_, amigaToken) = await CreateUserAndLoginAsync("amiga", Languages.Russian);
        var dino = Connect(dinoToken);
        var amiga = Connect(amigaToken);
        await dino.StartAsync();
        await amiga.StartAsync();
        return (dino, amiga);
    }

    private async Task<(UserProfile Profile, string AccessToken)> CreateUserAndLoginAsync(string userName, string language = Languages.PortugueseBrazil)
    {
        var profile = await _factory.CreateUserAsync(userName, language: language);
        using var client = _factory.CreateClient();
        var tokens = await _factory.LoginAsync(client, userName);
        return (profile, tokens.AccessToken);
    }

    private HubConnection Connect(string? accessToken, HttpTransportType transport = HttpTransportType.LongPolling)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, RoomRoutes.Hub), options =>
            {
                options.Transports = transport;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(accessToken);
                if (transport == HttpTransportType.WebSockets)
                {
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        var client = _factory.Server.CreateWebSocketClient();
                        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {accessToken}";
                        return await client.ConnectAsync(context.Uri, cancellationToken);
                    };
                }
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private static Inbox<T> Collect<T>(HubConnection connection, string method)
    {
        var inbox = new Inbox<T>();
        connection.On<T>(method, inbox.Add);
        return inbox;
    }

    private sealed class Inbox<T>
    {
        private readonly System.Threading.Channels.Channel<T> _channel = System.Threading.Channels.Channel.CreateUnbounded<T>();

        public bool HasAny => _channel.Reader.Count > 0;

        public void Add(T item) => _channel.Writer.TryWrite(item);

        public async Task<T> ReadAsync() => await _channel.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
    }
}
