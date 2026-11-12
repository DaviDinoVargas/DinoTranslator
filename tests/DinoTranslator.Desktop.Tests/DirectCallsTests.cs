using System.IO;
using System.Net;
using System.Net.Http;
using DinoTranslator.Desktop.Services;
using DinoTranslator.Desktop.Services.Plus;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

namespace DinoTranslator.Desktop.Tests;

/// <summary>Chamada 1:1 pela conta: tocar, atender, recusar, ocupado, sem resposta e outro aparelho.</summary>
public sealed class DirectCallsTests : IDisposable
{
    private static readonly Profile Dino = new("Dino", Languages.PortugueseBrazil, Gender.Male, Formality.Informal, "America/Sao_Paulo");
    private static readonly Profile Anna = new("Anna", Languages.Russian, Gender.Female, Formality.Informal, "Europe/Moscow");
    private static readonly Guid Channel = Guid.NewGuid();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dinotranslator-direta-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeServer _server = new();

    public DirectCallsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Liga_toca_atende_e_abre_a_chamada_nos_dois()
    {
        var dino = Person(Dino, "Anna Petrova");
        var anna = Person(Anna, "Dino Vargas");

        await dino.Calls.CallAsync(Channel);
        var ringing = dino.Calls.State;
        var incoming = anna.Calls.State;
        await anna.Calls.AnswerAsync();

        Assert.Equal(DirectCallPhase.Ringing, ringing?.Phase);
        Assert.Equal("Anna Petrova", ringing?.Name);
        Assert.Equal(new DirectCallState(DirectCallPhase.Incoming, Channel, "Dino Vargas", ringing!.CallId, Outgoing: false), incoming);
        Assert.Equal(CallTransport.Account, anna.Call.Transport);
        Assert.Contains("acceptAnswer", dino.Module.Calls);
        // Pela conta, nada vai para o serviço público de encontro.
        Assert.Equal(AutoConnect.Off, dino.Call.AutoConnect);
        Assert.Equal(AutoConnect.Off, anna.Call.AutoConnect);

        await dino.Call.OnChannelOpen();
        await anna.Call.OnChannelOpen();

        Assert.Null(dino.Calls.State);
        Assert.Null(anna.Calls.State);
        Assert.EndsWith("/chamada", dino.Navigation.Uri);
        Assert.EndsWith("/chamada", anna.Navigation.Uri);
        Assert.Equal("/plus", dino.Call.ReturnPath);
    }

    [Fact]
    public async Task Recusar_avisa_quem_ligou_e_desliga_o_convite()
    {
        var dino = Person(Dino, "Anna");
        var anna = Person(Anna, "Dino");

        await dino.Calls.CallAsync(Channel);
        await anna.Calls.DeclineAsync();

        Assert.Null(anna.Calls.State);
        Assert.Equal(DirectCallPhase.Ended, dino.Calls.State?.Phase);
        Assert.Equal("Anna recusou a chamada.", dino.Calls.State?.Message);
        Assert.Equal(CallStage.Idle, dino.Call.Stage);
        dino.Calls.Dismiss();
        Assert.Null(dino.Calls.State);
    }

    [Fact]
    public async Task Sem_resposta_quem_ligou_desiste_e_do_outro_lado_fica_chamada_perdida()
    {
        var dino = Person(Dino, "Anna");
        var anna = Person(Anna, "Dino");

        await dino.Calls.CallAsync(Channel);
        _time.Advance(DirectCalls.RingTimeout);

        Assert.Equal("Anna não atendeu.", dino.Calls.State?.Message);
        Assert.Equal(DirectCallPhase.Ended, anna.Calls.State?.Phase);
        Assert.Equal("Chamada perdida de Dino.", anna.Calls.State?.Message);
        Assert.Equal(CallStage.Idle, dino.Call.Stage);
    }

    [Fact]
    public async Task Quem_ja_esta_em_chamada_responde_ocupado_sozinho()
    {
        var dino = Person(Dino, "Anna");
        var anna = Person(Anna, "Dino");
        await anna.Call.CreateInviteAsync();

        await dino.Calls.CallAsync(Channel);

        Assert.Null(anna.Calls.State);
        Assert.Equal(CallStage.WaitingAnswer, anna.Call.Stage);
        Assert.Equal("Anna está em outra chamada.", dino.Calls.State?.Message);
    }

    [Fact]
    public async Task Sem_o_app_aberto_do_outro_lado_avisa_e_nao_fica_esperando()
    {
        var dino = Person(Dino, "Anna");
        _server.Online = false;

        await dino.Calls.CallAsync(Channel);

        Assert.Equal("Anna não está com o app aberto agora.", dino.Calls.State?.Message);
        Assert.Equal(CallStage.Idle, dino.Call.Stage);
    }

    [Fact]
    public async Task Atendida_em_outro_aparelho_para_de_tocar()
    {
        var dino = Person(Dino, "Anna");
        var anna = Person(Anna, "Dino");
        await dino.Calls.CallAsync(Channel);
        var callId = anna.Calls.State!.CallId;

        anna.Signaling.Receive(new CallSignalInfo(Channel, dino.Id, callId, CallSignal.Handled, null));

        Assert.Null(anna.Calls.State);
        _time.Advance(DirectCalls.IncomingTimeout);
        Assert.Null(anna.Calls.State);
    }

    [Fact]
    public async Task Quem_ligou_desiste_enquanto_conecta_e_o_outro_lado_desliga()
    {
        var dino = Person(Dino, "Anna");
        var anna = Person(Anna, "Dino");
        await dino.Calls.CallAsync(Channel);
        await anna.Calls.AnswerAsync();

        await dino.Calls.CancelAsync();

        Assert.Null(dino.Calls.State);
        Assert.Equal("Dino desistiu da chamada.", anna.Calls.State?.Message);
        Assert.Equal(CallStage.Idle, anna.Call.Stage);
    }

    [Fact]
    public async Task Convite_que_nao_e_de_chamada_e_ignorado()
    {
        var anna = Person(Anna, "Dino");

        anna.Signaling.Receive(new CallSignalInfo(Channel, Guid.NewGuid(), "0123456789abcdef0123456789abcdef", CallSignal.Ring, "não é um convite"));
        anna.Signaling.Receive(new CallSignalInfo(Channel, Guid.NewGuid(), "../outra", CallSignal.Ring, "x"));
        await Task.Yield();

        Assert.Null(anna.Calls.State);
        Assert.Empty(anna.Signaling.Sent);
    }

    private sealed record PersonUnderTest(Guid Id, CallSession Call, DirectCalls Calls, FakeSignaling Signaling, FakeModule Module, FakeNavigation Navigation);

    private PersonUnderTest Person(Profile profile, string peerName)
    {
        var folder = Path.Combine(_folder, profile.Name);
        Directory.CreateDirectory(folder);
        var profiles = new ProfileStore(Path.Combine(folder, "perfil.json"));
        profiles.Save(profile);
        var module = new FakeModule();
        var ui = new ImmediateDispatcher();
        var call = new CallSession(
            new FakeJs(module),
            profiles,
            new TitleBarState(),
            new TranslationSettingsStore(Path.Combine(folder, "motores.dat")),
            new ConnectionSettingsStore(Path.Combine(folder, "conexao.dat")),
            new SpeechEngineFactory(new ModelFolder(Path.Combine(folder, "modelos"))),
            // Encontro automático ligado: pela conta ele não deve ser usado.
            new RendezvousRelay(new HttpClient(new NoNetwork()), new Uri("https://encontro.exemplo/")),
            ui,
            _time);
        var id = Guid.NewGuid();
        var signaling = new FakeSignaling(_server, id, peerName);
        var navigation = new FakeNavigation();
        var calls = new DirectCalls(signaling, call, navigation, ui, _time);
        _server.People.Add(signaling);
        return new PersonUnderTest(id, call, calls, signaling, module, navigation);
    }

    /// <summary>O servidor: repassa o sinal à outra pessoa da conversa, se ela estiver conectada.</summary>
    private sealed class FakeServer
    {
        public bool Online { get; set; } = true;

        public List<FakeSignaling> People { get; } = [];
    }

    private sealed class FakeSignaling(FakeServer server, Guid me, string peerName) : ICallSignaling
    {
        public event Action<CallSignalInfo>? CallSignalReceived;

        public List<CallSignal> Sent { get; } = [];

        public Task<SignalResult> SendCallSignalAsync(Guid channelId, string callId, CallSignal signal, string? code)
        {
            Sent.Add(signal);
            if (server.Online)
            {
                foreach (var other in server.People.Where(p => p != this))
                {
                    other.Receive(new CallSignalInfo(channelId, me, callId, signal, code));
                }
            }
            return Task.FromResult(new SignalResult(true, server.Online));
        }

        public string? PeerName(Guid channelId) => peerName;

        public void Receive(CallSignalInfo signal) => CallSignalReceived?.Invoke(signal);
    }

    private sealed class FakeNavigation : NavigationManager
    {
        public FakeNavigation() => Initialize("https://app/", "https://app/");

        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
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
                "sendApp" or "sendAppFlushed" => true,
                _ => null,
            };
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Qualquer acesso à rede falha o teste: o encontro automático não pode ser usado.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Acesso à rede inesperado: {request.RequestUri}");
    }
}
