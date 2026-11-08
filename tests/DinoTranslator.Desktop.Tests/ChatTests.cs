using System.IO;
using System.Net.Http;
using DinoTranslator.Desktop.Services;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Desktop.Services.Translation.Local;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;

namespace DinoTranslator.Desktop.Tests;

/// <summary>Chat escrito e correção de falas: mensagens, histórico e a sessão dos dois lados.</summary>
public sealed class ChatTests : IDisposable
{
    private static readonly Profile Dino = new("Dino", Languages.PortugueseBrazil, Gender.Male, Formality.Informal, "America/Sao_Paulo");
    private static readonly Profile Anna = new("Anna", Languages.Russian, Gender.Female, Formality.Informal, "Europe/Moscow");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dinotranslator-chat-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ChatTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // ---- Mensagens --------------------------------------------------------------------

    [Fact]
    public void Mensagem_escrita_e_correcao_vao_e_voltam_pelo_canal()
    {
        Assert.Equal(new TextMessage("e-0a1b2c", "Oi, Anna!"), AppMessages.TryParse(AppMessages.Serialize(new TextMessage("e-0a1b2c", "Oi, Anna!"))));
        Assert.Equal(new CorrectionMessage("s3", "Oi, Ana!"), AppMessages.TryParse(AppMessages.Serialize(new CorrectionMessage("s3", "Oi, Ana!"))));
    }

    [Theory]
    [InlineData("""{"type":"text","id":"s1","text":"oi"}""")]
    [InlineData("""{"type":"text","id":"e-ABC","text":"oi"}""")]
    [InlineData("""{"type":"text","id":"e-1","text":"   "}""")]
    [InlineData("""{"type":"text","id":"e-1","text":"oi\u0007"}""")]
    [InlineData("""{"type":"text","id":"e-1","text":"oi‮io"}""")]
    [InlineData("""{"type":"correction","segmentId":"s 1","text":"oi"}""")]
    [InlineData("""{"type":"correction","segmentId":"s1","text":"..."}""")]
    public void Mensagem_escrita_invalida_e_descartada(string json)
    {
        Assert.Null(AppMessages.TryParse(json));
    }

    [Fact]
    public void Legenda_sem_correcao_nao_leva_o_campo_novo()
    {
        var caption = new CaptionMessage(CaptionSpeaker.Sender, "s1", 2, Languages.PortugueseBrazil, "oi", Languages.Russian, "привет", true);
        Assert.DoesNotContain("edited", AppMessages.Serialize(caption), StringComparison.OrdinalIgnoreCase);

        var corrected = caption with { Revision = 3, Edited = true };
        Assert.Equal(corrected, AppMessages.TryParse(AppMessages.Serialize(corrected)));
    }

    // ---- Histórico --------------------------------------------------------------------

    [Fact]
    public void Correcao_substitui_a_fala_no_mesmo_lugar_e_com_o_mesmo_horario()
    {
        var board = new CaptionBoard(_time);
        board.Apply(Final(CaptionOwner.Me, "s1", 1, "olá ana"));
        var saidAt = board.Transcript[0].UpdatedAt;
        _time.Advance(TimeSpan.FromMinutes(5));
        board.Apply(Final(CaptionOwner.Other, "s1", 1, "привет"));

        Assert.True(board.Apply(Final(CaptionOwner.Me, "s1", 2, "olá, Anna") with { Edited = true }));

        Assert.Equal(["olá, Anna", "привет"], board.Transcript.Select(c => c.SourceText));
        Assert.True(board.Transcript[0].Edited);
        Assert.Equal(saidAt, board.Transcript[0].UpdatedAt);
    }

    [Fact]
    public void Correcao_atrasada_ou_de_trecho_desconhecido_e_ignorada()
    {
        var board = new CaptionBoard(_time);
        board.Apply(Final(CaptionOwner.Me, "s1", 3, "texto"));

        Assert.False(board.Apply(Final(CaptionOwner.Me, "s1", 3, "velho") with { Edited = true }));
        Assert.False(board.Apply(Final(CaptionOwner.Me, "s9", 5, "outro") with { Edited = true }));
        Assert.False(board.Apply(Final(CaptionOwner.Other, "s1", 5, "de outra pessoa") with { Edited = true }));
        Assert.Equal("texto", Assert.Single(board.Transcript).SourceText);
    }

    // ---- Sessão -------------------------------------------------------------------------

    [Fact]
    public async Task Sem_traducao_a_mensagem_chega_como_esta_e_a_correcao_tambem()
    {
        var (host, hostJs) = Session(Dino);
        var (guest, guestJs) = Session(Anna);
        await ConnectAsync(host, hostJs, guest, guestJs);
        Assert.Equal(TranslatorSide.None, host.Translator);
        Assert.True(host.CanWrite);
        Assert.NotNull(host.WritingNotice);

        await host.SendTextAsync("  Oi, Anna!  ");
        var sent = Assert.Single(host.Captions.Transcript);
        Assert.Equal((CaptionOwner.Me, "Oi, Anna!", true), (sent.Owner, sent.SourceText, sent.Written));
        Assert.Null(sent.TranslatedText);

        await DeliverAsync(hostJs, guest);
        var received = Assert.Single(guest.Captions.Transcript);
        Assert.Equal((CaptionOwner.Other, "Oi, Anna!", sent.SegmentId), (received.Owner, received.SourceText, received.SegmentId));

        await host.CorrectAsync(sent.SegmentId, "Oi, Ana!");
        await DeliverAsync(hostJs, guest);
        Assert.Equal(("Oi, Ana!", true), (guest.Captions.Transcript[0].SourceText, guest.Captions.Transcript[0].Edited));

        // Ninguém corrige a fala dos outros: a Anna não pode mudar o que o Dino escreveu.
        await host.OnAppMessage(AppMessages.Serialize(new CorrectionMessage(sent.SegmentId, "texto trocado")));
        Assert.Equal("Oi, Ana!", host.Captions.Transcript[0].SourceText);
    }

    [Fact]
    public async Task Com_a_outra_pessoa_traduzindo_a_mensagem_espera_a_traducao()
    {
        var (guest, guestJs) = Session(Anna);
        var (host, hostJs) = Session(Dino);
        await guest.AcceptInviteAsync(await host.CreateInviteAsync());
        await guest.OnChannelOpen();
        await guest.OnAppMessage(AppMessages.Serialize(new HelloMessage(CanTranslate: true)));
        Assert.Equal(TranslatorSide.There, guest.Translator);
        Assert.Null(guest.WritingNotice);

        await guest.SendTextAsync("Привет!");
        var waiting = Assert.Single(guest.Captions.Visible());
        Assert.False(waiting.Final);
        var text = Assert.IsType<TextMessage>(AppMessages.TryParse(guestJs.Sent[^1]));
        Assert.Equal(waiting.SegmentId, text.Id);

        await guest.OnAppMessage(AppMessages.Serialize(new CaptionMessage(
            CaptionSpeaker.Receiver, text.Id, 1, Languages.Russian, "Привет!", Languages.PortugueseBrazil, "Olá!", true)));
        var final = Assert.Single(guest.Captions.Transcript);
        Assert.Equal((CaptionOwner.Me, "Olá!"), (final.Owner, final.TranslatedText));

        await guest.CorrectAsync(text.Id, "Привет, Дино!");
        Assert.Equal("Привет, Дино!", guest.PendingCorrection(text.Id));
        Assert.Equal(new CorrectionMessage(text.Id, "Привет, Дино!"), AppMessages.TryParse(guestJs.Sent[^1]));

        await guest.OnAppMessage(AppMessages.Serialize(new CaptionMessage(
            CaptionSpeaker.Receiver, text.Id, 2, Languages.Russian, "Привет, Дино!", Languages.PortugueseBrazil, "Olá, Dino!", true, Edited: true)));
        Assert.Null(guest.PendingCorrection(text.Id));
        Assert.Equal(("Привет, Дино!", "Olá, Dino!", true), (guest.Captions.Transcript[0].SourceText, guest.Captions.Transcript[0].TranslatedText, guest.Captions.Transcript[0].Edited));
        _ = hostJs;
    }

    [Fact]
    public async Task Mensagens_escritas_demais_da_outra_pessoa_sao_descartadas()
    {
        var (host, hostJs) = Session(Dino);
        var (guest, guestJs) = Session(Anna);
        await ConnectAsync(host, hostJs, guest, guestJs);

        for (var i = 0; i < CallSession.MaxWrittenPerMinute + 5; i++)
        {
            await guest.OnAppMessage(AppMessages.Serialize(new TextMessage($"e-{i:x6}", $"mensagem {i}")));
        }
        Assert.Equal(CallSession.MaxWrittenPerMinute, guest.Captions.Transcript.Count);

        _time.Advance(TimeSpan.FromMinutes(1.1));
        await guest.OnAppMessage(AppMessages.Serialize(new TextMessage("e-ffffff", "depois de um minuto")));
        Assert.Equal(CallSession.MaxWrittenPerMinute + 1, guest.Captions.Transcript.Count);
    }

    [Fact]
    public async Task Antes_de_conectar_nao_da_para_escrever()
    {
        var (host, _) = Session(Dino);
        await host.CreateInviteAsync();

        await host.SendTextAsync("cedo demais");

        Assert.False(host.CanWrite);
        Assert.Empty(host.Captions.Transcript);
    }

    private Caption Final(CaptionOwner owner, string id, int revision, string text) =>
        new(owner, id, revision, Languages.PortugueseBrazil, text, Languages.Russian, null, true, _time.GetUtcNow());

    private static async Task ConnectAsync(CallSession host, FakeModule hostJs, CallSession guest, FakeModule guestJs)
    {
        await host.AcceptAnswerAsync(await guest.AcceptInviteAsync(await host.CreateInviteAsync()));
        await host.OnChannelOpen();
        await guest.OnChannelOpen();
        await DeliverAsync(hostJs, guest);
        await DeliverAsync(guestJs, host);
    }

    /// <summary>Entrega ao outro lado o que um lado mandou pelo canal desde a última entrega.</summary>
    private static async Task DeliverAsync(FakeModule from, CallSession to)
    {
        while (from.Delivered < from.Sent.Count)
        {
            await to.OnAppMessage(from.Sent[from.Delivered++]);
        }
    }

    private (CallSession Session, FakeModule Module) Session(Profile profile)
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
            new ConnectionSettingsStore(Path.Combine(folder, "conexao.dat")),
            new SpeechEngineFactory(new ModelFolder(Path.Combine(folder, "modelos"))),
            new RendezvousRelay(new HttpClient(), server: null),
            new ImmediateDispatcher(),
            _time);
        return (session, module);
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

    /// <summary>call.js simulado que guarda o que foi mandado pelo canal do app.</summary>
    private sealed class FakeModule : IJSObjectReference
    {
        public List<string> Sent { get; } = [];

        public int Delivered { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier is "sendApp" or "sendAppFlushed" && args is [string message, ..])
            {
                Sent.Add(message);
            }
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
}
