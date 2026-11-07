using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using DinoTranslator.Desktop.Services;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

namespace DinoTranslator.Desktop.Tests;

/// <summary>Abertura da chamada com o WebRTC simulado: quem espera não desiste cedo demais.</summary>
public sealed class CallSessionTests : IDisposable
{
    private static readonly Profile Dino = new("Dino", Languages.PortugueseBrazil, Gender.Male, Formality.Informal, "America/Sao_Paulo");
    private static readonly Profile Anna = new("Anna", Languages.Russian, Gender.Female, Formality.Informal, "Europe/Moscow");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dinotranslator-sessao-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly MemoryRelay _relay = new();

    public CallSessionTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Convidado_nao_desiste_quando_a_primeira_tentativa_falha()
    {
        var (host, _) = Session(Dino);
        var (guest, _) = Session(Anna);
        var invite = await host.CreateInviteAsync();

        await guest.AcceptInviteAsync(invite);
        guest.OnConnectionState("failed");
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(CallStage.Connecting, guest.Stage);
        Assert.Null(guest.Problem);

        _time.Advance(CallCodes.Validity);
        Assert.Equal(CallStage.Ended, guest.Stage);
        Assert.Contains("expirou", guest.Problem);
    }

    [Fact]
    public async Task Anfitriao_tenta_pelo_prazo_e_explica_por_que_nao_conectou()
    {
        var (host, module) = Session(Dino, relay: false);
        var (guest, _) = Session(Anna, relay: false);
        var answer = await guest.AcceptInviteAsync(await host.CreateInviteAsync());

        await host.AcceptAnswerAsync(answer);
        host.OnConnectionState("failed");

        Assert.Equal(CallStage.Connecting, host.Stage);
        Assert.Contains("acceptAnswer", module.Calls);

        _time.Advance(CallSession.ConnectTimeout);
        Assert.Equal(CallStage.Ended, host.Stage);
        Assert.Contains("Não foi possível conectar com Anna", host.Problem);
    }

    [Fact]
    public async Task Conectado_o_prazo_de_abertura_nao_derruba_a_chamada()
    {
        var (host, _) = Session(Dino, relay: false);
        var (guest, _) = Session(Anna, relay: false);
        await host.AcceptAnswerAsync(await guest.AcceptInviteAsync(await host.CreateInviteAsync()));

        await host.OnChannelOpen();
        _time.Advance(CallSession.ConnectTimeout * 2);

        Assert.Equal(CallStage.Connected, host.Stage);
        Assert.Equal(12 + 2, host.SecurityCode?.Length);
    }

    [Fact]
    public async Task Resposta_volta_sozinha_pelo_encontro_automatico()
    {
        var (host, module) = Session(Dino);
        var (guest, _) = Session(Anna);
        var invite = await host.CreateInviteAsync();
        Assert.Equal(AutoConnect.Listening, host.AutoConnect);

        await guest.AcceptInviteAsync(invite);

        await WaitUntil(() => host.Stage == CallStage.Connecting);
        Assert.Equal(Anna.Name, host.Peer?.Name);
        Assert.Contains("acceptAnswer", module.Calls);
        await WaitUntil(() => guest.AutoConnect == AutoConnect.Sent);
        Assert.DoesNotContain(_relay.Published, m => m.Contains("Anna", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resposta_colada_depois_da_automatica_avisa_que_ja_chegou()
    {
        var (host, _) = Session(Dino);
        var (guest, _) = Session(Anna);
        var answer = await guest.AcceptInviteAsync(await host.CreateInviteAsync());
        await WaitUntil(() => host.Stage == CallStage.Connecting);

        var error = await Assert.ThrowsAsync<CallCodeException>(() => host.AcceptAnswerAsync(answer));
        Assert.Contains("já chegou", error.Message);
    }

    [Fact]
    public async Task Sem_o_servico_a_resposta_vai_pelo_mensageiro()
    {
        var (host, _) = Session(Dino, relay: false);
        var (guest, _) = Session(Anna, relay: false);
        await guest.AcceptInviteAsync(await host.CreateInviteAsync());

        Assert.Equal(AutoConnect.Off, host.AutoConnect);
        Assert.Equal(AutoConnect.Off, guest.AutoConnect);
        Assert.Equal(CallStage.WaitingAnswer, host.Stage);
    }

    private (CallSession Session, FakeModule Module) Session(Profile profile, bool relay = true)
    {
        var folder = Path.Combine(_folder, profile.Name);
        Directory.CreateDirectory(folder);
        var profiles = new ProfileStore(Path.Combine(folder, "perfil.json"));
        profiles.Save(profile);
        var module = new FakeModule();
        var session = new CallSession(
            new FakeJs(module),
            profiles,
            new TitleBarState(),
            new TranslationSettingsStore(Path.Combine(folder, "motores.dat")),
            new SpeechEngineFactory(new ModelFolder(Path.Combine(folder, "modelos"))),
            new RendezvousRelay(new HttpClient(_relay), relay ? new Uri("https://encontro.exemplo/") : null),
            new ImmediateDispatcher(),
            _time);
        return (session, module);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJs(FakeModule module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)module);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>call.js simulado: devolve SDPs válidas e registra o que foi pedido.</summary>
    private sealed class FakeModule : IJSObjectReference
    {
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            object? result = identifier switch
            {
                "createInvite" or "acceptInvite" or "localSdp" => CallCodesTests.Sdp,
                "sendApp" => true,
                _ => null,
            };
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Serviço de mensagens em memória com o protocolo do ntfy.</summary>
    private sealed class MemoryRelay : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<(string Topic, string Message)> _messages = [];

        public IReadOnlyList<string> Published
        {
            get
            {
                lock (_gate)
                {
                    return _messages.Select(m => m.Message).ToList();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var topic = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            if (request.Method == HttpMethod.Post)
            {
                var message = await request.Content!.ReadAsStringAsync(cancellationToken);
                lock (_gate)
                {
                    _messages.Add((topic, message));
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            var lines = new StringBuilder();
            lock (_gate)
            {
                for (var i = 0; i < _messages.Count; i++)
                {
                    if (_messages[i].Topic == topic)
                    {
                        lines.Append($$"""{"id":"m{{i}}","event":"message","topic":"{{topic}}","message":"{{_messages[i].Message}}"}""").Append('\n');
                    }
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lines.ToString()) };
        }
    }
}
