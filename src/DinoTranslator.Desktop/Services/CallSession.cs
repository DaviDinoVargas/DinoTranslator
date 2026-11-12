using System.Security.Cryptography;
using DinoTranslator.Desktop.Services.Audio;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Call;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DinoTranslator.Desktop.Services;

public enum CallStage
{
    Idle,
    WaitingAnswer,
    Connecting,
    Connected,
    Ended,
}

public enum CallRole
{
    Host,
    Guest,
}

/// <summary>Andamento do encontro automático (a resposta que volta sem o mensageiro).</summary>
public enum AutoConnect
{
    /// <summary>Desligado no appsettings.json, ou ainda não começou.</summary>
    Off,

    /// <summary>Anfitrião: esperando a resposta chegar sozinha.</summary>
    Listening,

    /// <summary>Convidado: enviando a resposta.</summary>
    Sending,

    /// <summary>Convidado: resposta entregue ao serviço.</summary>
    Sent,

    /// <summary>O serviço não respondeu: a resposta vai pelo mensageiro.</summary>
    Unavailable,
}

/// <summary>Qualidade da câmera enviada (o nome é o usado pela camada de mídia).</summary>
public enum VideoQuality
{
    /// <summary>1080p, até 4 Mb/s.</summary>
    Maxima,

    /// <summary>720p, até 2 Mb/s.</summary>
    Alta,

    /// <summary>480p, até 0,9 Mb/s.</summary>
    Media,

    /// <summary>360p, até 0,45 Mb/s: para redes fracas.</summary>
    Baixa,
}

/// <summary>O que deu para ligar ao começar a chamada.</summary>
public sealed record LocalMedia(bool Camera, bool Microphone);

public enum ScreenMode
{
    /// <summary>Texto e código: mantém a resolução e reduz os quadros por segundo se preciso.</summary>
    Detail,

    /// <summary>Vídeo e jogos: mantém os quadros por segundo e reduz a resolução se preciso.</summary>
    Motion,
}

/// <summary>
/// Uma chamada por vez: monta os códigos, conduz a camada de mídia (call.js) e guarda o
/// estado que as telas mostram. Vive enquanto a janela do app estiver aberta.
/// </summary>
public sealed class CallSession(
    IJSRuntime js,
    ProfileStore profiles,
    TitleBarState titleBar,
    TranslationSettingsStore translationSettings,
    ConnectionSettingsStore connectionSettings,
    SpeechEngineFactory engines,
    RendezvousRelay relay,
    IUiDispatcher ui,
    TimeProvider time,
    EngineChoiceStore? engineChoices = null) : IAsyncDisposable
{
    /// <summary>Intervalo mínimo entre legendas provisórias enviadas ao outro lado; finais vão sempre.</summary>
    private static readonly TimeSpan PartialCaptionInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Depois que o anfitrião tem a resposta, os dois se procuram por até este tempo.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private const int MaxSpeechChunkBytes = 64 * 1024;

    /// <summary>Mensagens escritas e correções aceitas da outra pessoa por minuto (cada uma custa uma tradução).</summary>
    public const int MaxWrittenPerMinute = 40;

    /// <summary>Correção sem resposta de quem traduz deixa de aparecer como pendente depois disto.</summary>
    private static readonly TimeSpan PendingCorrectionTimeout = TimeSpan.FromSeconds(20);

    private IJSObjectReference? _module;
    private DotNetObjectReference<CallSession>? _self;
    private CallCode? _pendingInvite;
    private string? _remoteSdp;
    private HelloMessage? _peerHello;
    private bool _mediaStarted;
    private LiveTranslation? _translation;
    private readonly Dictionary<CaptionOwner, DateTimeOffset> _lastPartialSent = [];
    private CancellationTokenSource? _rendezvous;
    private ITimer? _openingDeadline;
    private int _attempt;
    private readonly Queue<DateTimeOffset> _writtenReceived = new();
    private EngineChoice? _engineChoice;
    private bool _enginesChanged;
    private int _translationGeneration;
    private readonly Dictionary<string, (string Text, DateTimeOffset At)> _pendingCorrections = [];

    public event Action? Changed;

    public CallStage Stage { get; private set; }

    public CallRole? Role { get; private set; }

    public Profile? Peer { get; private set; }

    public DateTimeOffset? InviteExpiresAt { get; private set; }

    /// <summary>12 dígitos para os dois conferirem em voz alta.</summary>
    public string? SecurityCode { get; private set; }

    public int? RoundTripMs { get; private set; }

    public string? Route { get; private set; }

    public string? Problem { get; private set; }

    public AutoConnect AutoConnect { get; private set; }

    public bool SharingScreen { get; private set; }

    public bool RemoteScreen { get; private set; }

    public ScreenMode ScreenMode { get; private set; } = ScreenMode.Detail;

    public VideoQuality VideoQuality { get; private set; } = VideoQuality.Maxima;

    /// <summary>Janela flutuante aberta (sempre por cima, com a imagem e a legenda da outra pessoa).</summary>
    public bool Floating { get; private set; }

    /// <summary>Ex.: "2560×1440 · 30 fps · AV1".</summary>
    public string? ScreenStats { get; private set; }

    public CaptionBoard Captions { get; } = new(time);

    public TranslatorSide Translator { get; private set; }

    /// <summary>Aviso sobre a tradução para mostrar na chamada (desligada, erro do motor etc.).</summary>
    public string? TranslationNotice { get; private set; }

    /// <summary>Médias de tempo das últimas frases com a combinação de motores atual.</summary>
    public EngineStats EngineStats { get; } = new();

    /// <summary>Trocando a combinação de motores (carregando modelos).</summary>
    public bool EnginesBusy { get; private set; }

    /// <summary>Por que a combinação escolhida não pôde ser usada, ou <c>null</c>.</summary>
    public string? EnginesProblem { get; private set; }

    /// <summary>A combinação em uso, quando este computador traduz com o motor local; senão, <c>null</c>.</summary>
    public EngineChoice? Engines =>
        Translator == TranslatorSide.Here && translationSettings.Current.Provider == TranslationProvider.Local ? CurrentEngineChoice() : null;

    /// <summary>O que dá para escolher neste computador (modelos e componentes instalados).</summary>
    public EngineAvailability EngineOptions => engines.Availability(translationSettings.Current);

    /// <summary>
    /// Troca a combinação de motores no meio da chamada: a tradução para, as peças que saem são
    /// liberadas e ela volta com as novas (os primeiros segundos carregam os modelos).
    /// </summary>
    public async Task ChangeEnginesAsync(EngineChoice choice)
    {
        if (Engines is null || choice == _engineChoice)
        {
            return;
        }
        // Troca no meio de outra: vale a mais nova, logo que a atual terminar.
        _engineChoice = choice;
        engineChoices?.Save(choice);
        if (EnginesBusy)
        {
            _enginesChanged = true;
            return;
        }
        EnginesBusy = true;
        EnginesProblem = null;
        Notify();
        try
        {
            do
            {
                _enginesChanged = false;
                await StopTranslationAsync();
                await engines.RetainAsync(CurrentEngineChoice());
                EngineStats.Clear();
                TranslationNotice = null;
                EnginesProblem = null;
                await MaybeStartTranslationAsync();
            }
            while (_enginesChanged && Stage == CallStage.Connected);
        }
        finally
        {
            EnginesBusy = false;
            Notify();
        }
    }

    /// <summary>Dá para escrever no chat: a chamada está conectada e os dois já se apresentaram.</summary>
    public bool CanWrite => Stage == CallStage.Connected && _peerHello is not null;

    /// <summary>O que é escrito vai sem tradução (e por quê); <c>null</c> quando vai traduzido.</summary>
    public string? WritingNotice => Translator switch
    {
        TranslatorSide.None => "Sem tradução nesta chamada: o que você escrever vai como está.",
        TranslatorSide.Here when _translation is { CanTranslateText: false } =>
            "O motor de tradução deste computador só traduz fala: o que você escrever vai sem tradução.",
        _ => null,
    };

    /// <summary>Correção sua ainda sem a nova tradução (texto novo), ou <c>null</c>.</summary>
    public string? PendingCorrection(string segmentId) =>
        _pendingCorrections.TryGetValue(segmentId, out var pending) && time.GetUtcNow() - pending.At < PendingCorrectionTimeout
            ? pending.Text
            : null;

    /// <summary>
    /// Mensagem escrita no chat. Ela aparece na hora; quem traduz devolve a tradução e as duas
    /// telas mostram o mesmo balão.
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        var cleaned = LiveTranslation.Clean(text);
        if (!CanWrite || !AppMessages.IsWrittenText(cleaned) || Peer is not { } peer || profiles.Current is not { } me)
        {
            return;
        }
        var id = TextMessage.IdPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var waiting = new Caption(CaptionOwner.Me, id, 0, me.Language, cleaned, peer.Language, null, Final: false, time.GetUtcNow());
        switch (Translator)
        {
            case TranslatorSide.Here:
                Captions.Apply(waiting);
                Notify();
                var final = waiting with { Revision = 1, TranslatedText = await TranslateWrittenAsync(cleaned, CaptionOwner.Me), Final = true };
                if (Captions.Apply(final))
                {
                    await SendAppAsync(CaptionBoard.ToMessage(final));
                }
                break;
            case TranslatorSide.There:
                Captions.Apply(waiting);
                await SendAppAsync(new TextMessage(id, cleaned));
                break;
            default:
                Captions.Apply(waiting with { Revision = 1, Final = true });
                await SendAppAsync(new TextMessage(id, cleaned));
                break;
        }
        Notify();
    }

    /// <summary>Corrige o texto de uma fala (ou mensagem) sua; a tradução é refeita para os dois.</summary>
    public async Task CorrectAsync(string segmentId, string text)
    {
        var cleaned = LiveTranslation.Clean(text);
        if (!CanWrite || !AppMessages.IsWrittenText(cleaned)
            || Captions.Find(CaptionOwner.Me, segmentId) is not { } current || current.SourceText == cleaned)
        {
            return;
        }
        switch (Translator)
        {
            case TranslatorSide.Here:
                _pendingCorrections[segmentId] = (cleaned, time.GetUtcNow());
                Notify();
                var translated = await TranslateWrittenAsync(cleaned, CaptionOwner.Me);
                _pendingCorrections.Remove(segmentId);
                var latest = Captions.Find(CaptionOwner.Me, segmentId) ?? current;
                var corrected = latest with { Revision = latest.Revision + 1, SourceText = cleaned, TranslatedText = translated, Edited = true };
                if (Captions.Apply(corrected))
                {
                    await SendAppAsync(CaptionBoard.ToMessage(corrected));
                }
                break;
            case TranslatorSide.There:
                _pendingCorrections[segmentId] = (cleaned, time.GetUtcNow());
                await SendAppAsync(new CorrectionMessage(segmentId, cleaned));
                break;
            default:
                Captions.Apply(current with { Revision = current.Revision + 1, SourceText = cleaned, Edited = true });
                await SendAppAsync(new CorrectionMessage(segmentId, cleaned));
                break;
        }
        Notify();
    }

    /// <summary>Anfitrião: gera o convite. Cada convite aceita uma única resposta.</summary>
    public async Task<string> CreateInviteAsync()
    {
        var profile = RequireProfile();
        await ResetAsync();
        var module = await StartPeerAsync(polite: false);
        var sdp = await module.InvokeAsync<string>("createInvite");

        var now = time.GetUtcNow();
        _pendingInvite = new CallCode(CallCodes.CurrentVersion, CallCodeKind.Invite, CallCodes.NewCallId(), now, profile, sdp);
        var text = CallCodes.Encode(_pendingInvite);

        Role = CallRole.Host;
        Stage = CallStage.WaitingAnswer;
        InviteExpiresAt = now + CallCodes.Validity;
        ListenForAnswer(new CallRendezvous(_pendingInvite.CallId), now);
        Notify();
        return text;
    }

    /// <summary>Anfitrião: lê a resposta do convidado e conecta.</summary>
    public async Task AcceptAnswerAsync(string text)
    {
        if (Stage is CallStage.Connecting or CallStage.Connected)
        {
            throw new CallCodeException($"A resposta de {Peer?.Name ?? "a outra pessoa"} já chegou. Conectando…");
        }
        if (Stage != CallStage.WaitingAnswer || _pendingInvite is not { } invite || _module is null)
        {
            throw new CallCodeException("Este convite não vale mais. Crie um novo convite.");
        }

        var now = time.GetUtcNow();
        if (now - invite.CreatedAt > CallCodes.Validity)
        {
            throw new CallCodeException("Este convite expirou. Crie outro.");
        }
        var answer = CallCodes.Decode(text, CallCodeKind.Answer, now);
        if (answer.CallId != invite.CallId)
        {
            throw new CallCodeException("Esta resposta é de outro convite.");
        }

        _pendingInvite = null;
        StopRendezvous();
        Peer = answer.Profile;
        _remoteSdp = answer.Sdp;
        Stage = CallStage.Connecting;
        Problem = null;
        StartOpeningDeadline(ConnectTimeout,
            $"Não foi possível conectar com {answer.Profile.Name}: as redes de vocês não deixaram a ligação direta passar. "
            + "Criem um novo convite; se continuar, um de vocês pode tentar outra rede ou configurar uma retransmissão.");
        Notify();
        await _module.InvokeVoidAsync("acceptAnswer", answer.Sdp);
    }

    /// <summary>Convidado: lê o convite e gera a resposta.</summary>
    public async Task<string> AcceptInviteAsync(string text)
    {
        var profile = RequireProfile();
        var now = time.GetUtcNow();
        var invite = CallCodes.Decode(text, CallCodeKind.Invite, now);

        await ResetAsync();
        var module = await StartPeerAsync(polite: true);
        var sdp = await module.InvokeAsync<string>("acceptInvite", invite.Sdp);
        var answer = CallCodes.Encode(new CallCode(CallCodes.CurrentVersion, CallCodeKind.Answer, invite.CallId, now, profile, sdp));

        Role = CallRole.Guest;
        Peer = invite.Profile;
        _remoteSdp = invite.Sdp;
        Stage = CallStage.Connecting;
        // O convidado espera o anfitrião enquanto o convite valer; quem decide desistir é ele.
        var remaining = invite.CreatedAt + CallCodes.Validity - now;
        StartOpeningDeadline(remaining > ConnectTimeout ? remaining : ConnectTimeout,
            $"O convite de {invite.Profile.Name} expirou sem a chamada conectar. Peçam um novo convite.");
        PublishAnswer(new CallRendezvous(invite.CallId), answer);
        Notify();
        return answer;
    }

    public async Task<LocalMedia> StartMediaAsync(ElementReference localVideo, ElementReference remoteVideo, bool highFidelity)
    {
        if (_module is null || Stage != CallStage.Connected)
        {
            return new LocalMedia(false, false);
        }
        var media = await _module.InvokeAsync<LocalMedia>("startMedia", localVideo, remoteVideo, highFidelity);
        VideoQuality = ParseQuality(await _module.InvokeAsync<string>("currentVideoQuality"));
        Audio = await _module.InvokeAsync<CallAudio>("audioState");
        _mediaStarted = true;
        await MaybeStartTranslationAsync();
        Notify();
        return media;
    }

    /// <summary>Supressão de ruído do microfone nesta chamada (escolhas e o modo rodando).</summary>
    public NoiseState? Noise { get; private set; }

    public async Task RefreshNoiseAsync()
    {
        if (_module is not null)
        {
            Noise = (await _module.InvokeAsync<NoiseState>("noiseState")).Sanitized();
            Notify();
        }
    }

    public async Task SetNoiseAsync(NoiseSettings settings)
    {
        if (_module is null)
        {
            return;
        }
        Noise = Noise is null ? new NoiseState(settings.Sanitized(), null, null) : Noise with { Settings = settings.Sanitized() };
        Notify();
        try
        {
            await _module.InvokeAsync<NoiseSettings>("setNoise", settings.Sanitized());
        }
        catch (JSException)
        {
            // O microfone não reabriu com as novas opções: o painel mostra o que ficou valendo.
        }
        await RefreshNoiseAsync();
    }

    public async Task BindNoiseMeterAsync(ElementReference fill, ElementReference mark)
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("bindNoiseMeter", fill, mark);
        }
    }

    public async Task UnbindNoiseMeterAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("bindNoiseMeter", null, null);
        }
    }

    [JSInvokable]
    public void OnNoiseStatus(string mode, string? problem)
    {
        if (Noise is not null && NoiseModes.IsKnown(mode))
        {
            Noise = (Noise with { Mode = mode, Problem = problem }).Sanitized();
            Notify();
        }
    }

    public async Task SetVideoQualityAsync(VideoQuality quality)
    {
        VideoQuality = quality;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setVideoQuality", QualityName(quality));
        }
        Notify();
    }

    /// <summary>Liga ou desliga o som da outra pessoa neste computador (a tradução continua).</summary>
    public async Task SetRemoteAudioAsync(bool enabled)
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setRemoteAudio", enabled);
            Audio = Audio with { PersonMuted = !enabled };
            Notify();
        }
    }

    /// <summary>
    /// Volumes deste computador: a voz da pessoa e o som da tela que ela compartilha, cada um com
    /// volume (0–100) e mudo. Guardados para as próximas chamadas.
    /// </summary>
    public CallAudio Audio { get; private set; } = CallAudio.Default;

    public async Task SetVolumeAsync(AudioSource source, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        Audio = source == AudioSource.Screen ? Audio with { ScreenVolume = percent } : Audio with { PersonVolume = percent };
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setVolume", source == AudioSource.Screen ? "screen" : "person", percent);
        }
        Notify();
    }

    public async Task SetMutedAsync(AudioSource source, bool muted)
    {
        Audio = source == AudioSource.Screen ? Audio with { ScreenMuted = muted } : Audio with { PersonMuted = muted };
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setMuted", source == AudioSource.Screen ? "screen" : "person", muted);
        }
        Notify();
    }

    /// <summary>Corta ou volta o som da própria tela que está sendo compartilhada.</summary>
    public async Task SetScreenAudioSendingAsync(bool enabled)
    {
        Audio = Audio with { SendingScreenAudio = enabled };
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setScreenAudioSending", enabled);
        }
        Notify();
    }

    [JSInvokable]
    public void OnRemoteScreenAudio(bool hasAudio)
    {
        Audio = Audio with { RemoteScreenHasAudio = hasAudio };
        Notify();
    }

    [JSInvokable]
    public void OnLocalScreenAudio(bool hasAudio)
    {
        Audio = Audio with { SharingHasAudio = hasAudio };
        Notify();
    }

    /// <summary>Abre a janela flutuante; <c>false</c> se este WebView2 não oferece.</summary>
    public async Task<bool> OpenFloatingAsync()
    {
        if (_module is null || _self is null || Stage != CallStage.Connected || Floating)
        {
            return Floating;
        }
        if (!await _module.InvokeAsync<bool>("canFloat"))
        {
            return false;
        }
        await _module.InvokeVoidAsync("openFloating", _self, RemoteScreen);
        Floating = true;
        Notify();
        return true;
    }

    public async Task CloseFloatingAsync()
    {
        if (_module is not null && Floating)
        {
            await _module.InvokeVoidAsync("closeFloating");
        }
        Floating = false;
        Notify();
    }

    public async Task UpdateFloatingAsync(string? main, string? secondary)
    {
        if (_module is not null && Floating)
        {
            await _module.InvokeVoidAsync("updateFloating", main, secondary, RemoteScreen);
        }
    }

    [JSInvokable]
    public void OnFloatingClosed()
    {
        Floating = false;
        Notify();
    }

    internal static string QualityName(VideoQuality quality) => quality.ToString().ToLowerInvariant();

    internal static VideoQuality ParseQuality(string? name) =>
        Enum.TryParse<VideoQuality>(name, ignoreCase: true, out var quality) && Enum.IsDefined(quality) ? quality : VideoQuality.Maxima;

    public async Task SetMicrophoneAsync(bool enabled)
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setTrackEnabled", "audio", enabled);
        }
    }

    public async Task SetCameraAsync(bool enabled)
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setTrackEnabled", "video", enabled);
        }
    }

    /// <summary>Abre o seletor de tela ou janela do Windows; cancelar o seletor não é erro.</summary>
    public async Task StartScreenShareAsync()
    {
        if (_module is null || Stage != CallStage.Connected || SharingScreen)
        {
            return;
        }
        try
        {
            await _module.InvokeVoidAsync("startScreenShare", ModeName(ScreenMode));
        }
        catch (JSException ex) when (ex.Message.Contains("NotAllowedError", StringComparison.Ordinal)
            || ex.Message.Contains("AbortError", StringComparison.Ordinal))
        {
            // A pessoa fechou o seletor sem escolher nada.
        }
        catch (JSException)
        {
            Problem = "Não foi possível compartilhar a tela.";
            Notify();
        }
    }

    public async Task StopScreenShareAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("stopScreenShare");
        }
    }

    public async Task SetScreenModeAsync(ScreenMode mode)
    {
        ScreenMode = mode;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setScreenMode", ModeName(mode));
        }
        Notify();
    }

    /// <summary>Liga os vídeos das telas: a da outra pessoa e a prévia da sua.</summary>
    public async Task BindScreensAsync(ElementReference remote, ElementReference local)
    {
        if (_module is not null && Stage == CallStage.Connected)
        {
            await _module.InvokeVoidAsync("bindRemoteScreen", remote);
            await _module.InvokeVoidAsync("bindLocalScreen", local);
        }
    }

    public async Task HangUpAsync()
    {
        if (_module is not null && Stage == CallStage.Connected)
        {
            await _module.InvokeAsync<bool>("sendAppFlushed", AppMessages.Serialize(new ByeMessage()));
        }
        await ResetAsync();
        Notify();
    }

    [JSInvokable]
    public async Task OnChannelOpen()
    {
        var localSdp = _module is null ? null : await _module.InvokeAsync<string?>("localSdp");
        SecurityCode = localSdp is not null && _remoteSdp is not null
            ? CallCodes.SecurityCode(SessionDescriptions.Fingerprint(localSdp), SessionDescriptions.Fingerprint(_remoteSdp))
            : null;
        Stage = CallStage.Connected;
        Problem = null;
        StopOpeningDeadline();
        StopRendezvous();
        titleBar.SetPeer(Peer?.Name, Peer?.TimeZone);
        Notify();
        await SendAppAsync(new HelloMessage(engines.IsReady(translationSettings.Current)));
    }

    [JSInvokable]
    public void OnConnectionState(string state)
    {
        switch (state)
        {
            case "connected":
                Problem = null;
                break;
            case "disconnected":
                Problem = "Conexão instável. Tentando recuperar…";
                break;
            case "failed" when Stage == CallStage.Connected:
                End("A conexão caiu. Para voltar, criem um novo convite.");
                break;
            case "failed":
                // Ainda abrindo: o outro lado pode chegar depois e a conexão se recupera
                // sozinha. Quem encerra é o prazo de abertura.
                break;
        }
        Notify();
    }

    [JSInvokable]
    public void OnStats(int roundTripMs, string candidateType)
    {
        RoundTripMs = roundTripMs >= 0 ? roundTripMs : null;
        Route = candidateType switch
        {
            "host" => "rede local",
            "srflx" or "prflx" => "direta",
            "relay" => "retransmitida",
            _ => null,
        };
        titleBar.SetStatus(RoundTripMs is { } rtt ? $"ping {rtt} ms" : null);
        Notify();
    }

    [JSInvokable]
    public async Task OnAppMessage(string text)
    {
        switch (AppMessages.TryParse(text))
        {
            case HelloMessage hello when _peerHello is null && Role is { } role:
                _peerHello = hello;
                Translator = TranslatorChoice.Decide(role, engines.IsReady(translationSettings.Current), hello.CanTranslate);
                TranslationNotice = Translator == TranslatorSide.None
                    ? "Tradução desligada: nenhum dos dois configurou o motor de tradução (Início › Tradução)."
                    : null;
                Notify();
                await MaybeStartTranslationAsync();
                break;
            // Legendas só são aceitas de quem foi escolhido para traduzir.
            case CaptionMessage caption when Translator == TranslatorSide.There:
                var received = CaptionBoard.FromMessage(caption, time.GetUtcNow());
                if (received is { Edited: true, Owner: CaptionOwner.Me })
                {
                    _pendingCorrections.Remove(received.SegmentId);
                }
                if (Captions.Apply(received))
                {
                    Notify();
                }
                break;
            // Escrita e correções vêm de quem não traduz; com limite, porque cada uma é uma tradução.
            case TextMessage written when Translator != TranslatorSide.There && AcceptWritten():
                await OnPeerTextAsync(written);
                break;
            case CorrectionMessage correction when Translator != TranslatorSide.There && AcceptWritten():
                await OnPeerCorrectionAsync(correction);
                break;
            case ByeMessage:
                End($"{Peer?.Name ?? "A outra pessoa"} encerrou a chamada.");
                Notify();
                break;
        }
    }

    /// <summary>Áudio da fala em 16 kHz, vindo da captura do WebView2.</summary>
    [JSInvokable]
    public void OnSpeechAudio(string source, byte[] pcm)
    {
        if (pcm.Length is > 0 and <= MaxSpeechChunkBytes)
        {
            _translation?.WriteAudio(source, pcm);
        }
    }

    [JSInvokable]
    public void OnLocalScreen(bool active)
    {
        SharingScreen = active;
        ScreenStats = null;
        Notify();
    }

    [JSInvokable]
    public void OnRemoteScreen(bool active)
    {
        RemoteScreen = active;
        if (!SharingScreen)
        {
            ScreenStats = null;
        }
        Notify();
    }

    [JSInvokable]
    public void OnScreenStats(bool sending, int width, int height, int framesPerSecond, string codec)
    {
        if (sending != SharingScreen || width <= 0 || height <= 0)
        {
            return;
        }
        var codecName = codec.Length is > 0 and <= 16 ? $" · {codec}" : string.Empty;
        ScreenStats = $"{width}×{height} · {framesPerSecond} fps{codecName}";
        Notify();
    }

    [JSInvokable]
    public void OnMediaError(string message)
    {
        // Encerrada, a conexão fechando gera erros de renegociação que não interessam a ninguém
        // e não podem apagar o motivo do encerramento.
        if (Stage != CallStage.Connected)
        {
            return;
        }
        Problem = "Não foi possível ajustar o áudio ou o vídeo da chamada.";
#if DEBUG
        // Em desenvolvimento, o motivo técnico junto (o teste de ponta a ponta lê daqui).
        Problem += " [" + (message.Length > 300 ? message[..300] : message) + "]";
#endif
        Notify();
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("hangup");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // A janela já fechou.
            }
        }
        StopRendezvous();
        StopOpeningDeadline();
        _self?.Dispose();
    }

    private Profile RequireProfile() =>
        profiles.Current ?? throw new CallCodeException("Preencha o seu perfil antes de chamar.");

    private async Task<IJSObjectReference> StartPeerAsync(bool polite)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/call.js");
        _self ??= DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("create", _self, polite, RelayFor(connectionSettings.Current));
        return _module;
    }

    /// <summary>Liga a tradução quando este computador foi escolhido e a mídia já começou.</summary>
    private async Task MaybeStartTranslationAsync()
    {
        var settings = translationSettings.Current;
        if (Translator != TranslatorSide.Here || _translation is not null || !_mediaStarted
            || _module is null || _self is null || Peer is not { } peer || profiles.Current is not { } me
            || engines.Create(settings, settings.Provider == TranslationProvider.Local ? CurrentEngineChoice() : null) is not { } engine)
        {
            return;
        }

        var translation = new LiveTranslation(engine, me, peer, time, _translationGeneration++);
        translation.Measured += timing =>
        {
            EngineStats.Add(timing);
            _ = ui.InvokeAsync(Notify);
        };
        translation.CaptionReady += caption => _ = ui.InvokeAsync(() => OnLocalCaption(caption));
        translation.Failed += message => _ = ui.InvokeAsync(() =>
        {
            TranslationNotice = message;
            Notify();
        });
        _translation = translation;
        try
        {
            await translation.StartAsync([], CancellationToken.None);
            await _module.InvokeVoidAsync("enableSpeechCapture", _self);
        }
        catch (Exception ex)
        {
            _translation = null;
            await translation.DisposeAsync();
            TranslationNotice = "Não foi possível ligar a tradução. A chamada continua, sem legendas.";
            // Motor extra ou modelo com problema: o motivo aparece no painel de motores.
            EnginesProblem = ex is Translation.Extras.PythonEngineException or Translation.Local.ModelDownloadException ? ex.Message : null;
            Notify();
        }
    }

    /// <summary>
    /// A combinação desta chamada: a escolhida antes (guardada) ou a do ajuste. Opção que não está
    /// mais instalada volta ao padrão.
    /// </summary>
    private EngineChoice CurrentEngineChoice()
    {
        if (_engineChoice is { } chosen)
        {
            return chosen;
        }
        var settings = translationSettings.Current;
        var fallback = EngineChoice.For(settings);
        var stored = engineChoices?.Load()?.Sanitized(settings) ?? fallback;
        var options = EngineOptions;
        _engineChoice = new EngineChoice(
            options.Detectors.Contains(stored.Detector) ? stored.Detector : fallback.Detector,
            stored.PauseMs,
            options.Recognizers.Contains(stored.Recognizer) ? stored.Recognizer : fallback.Recognizer,
            options.Translators.Contains(stored.Translator) ? stored.Translator : fallback.Translator,
            stored.Careful);
        return _engineChoice;
    }

    private void OnLocalCaption(Caption caption)
    {
        if (_translation is null || !Captions.Apply(caption))
        {
            return;
        }
        var now = time.GetUtcNow();
        var send = caption.Final
            || !_lastPartialSent.TryGetValue(caption.Owner, out var last)
            || now - last >= PartialCaptionInterval;
        if (send)
        {
            _lastPartialSent[caption.Owner] = now;
            _ = SendAppAsync(CaptionBoard.ToMessage(caption));
        }
        Notify();
    }

    private bool AcceptWritten()
    {
        var now = time.GetUtcNow();
        while (_writtenReceived.Count > 0 && now - _writtenReceived.Peek() > TimeSpan.FromMinutes(1))
        {
            _writtenReceived.Dequeue();
        }
        if (_writtenReceived.Count >= MaxWrittenPerMinute)
        {
            return false;
        }
        _writtenReceived.Enqueue(now);
        return true;
    }

    private async Task OnPeerTextAsync(TextMessage written)
    {
        if (Peer is not { } peer || profiles.Current is not { } me || Captions.Find(CaptionOwner.Other, written.Id) is not null)
        {
            return;
        }
        var caption = new Caption(CaptionOwner.Other, written.Id, 1, peer.Language, written.Text, me.Language, null, Final: true, time.GetUtcNow());
        if (Translator == TranslatorSide.Here)
        {
            caption = caption with { TranslatedText = await TranslateWrittenAsync(written.Text, CaptionOwner.Other) };
        }
        if (Captions.Apply(caption))
        {
            if (Translator == TranslatorSide.Here)
            {
                await SendAppAsync(CaptionBoard.ToMessage(caption));
            }
            Notify();
        }
    }

    private async Task OnPeerCorrectionAsync(CorrectionMessage correction)
    {
        if (Captions.Find(CaptionOwner.Other, correction.SegmentId) is not { } current || current.SourceText == correction.Text)
        {
            return;
        }
        var translated = Translator == TranslatorSide.Here ? await TranslateWrittenAsync(correction.Text, CaptionOwner.Other) : null;
        var latest = Captions.Find(CaptionOwner.Other, correction.SegmentId) ?? current;
        var corrected = latest with { Revision = latest.Revision + 1, SourceText = correction.Text, TranslatedText = translated, Edited = true };
        if (Captions.Apply(corrected))
        {
            if (Translator == TranslatorSide.Here)
            {
                await SendAppAsync(CaptionBoard.ToMessage(corrected));
            }
            Notify();
        }
    }

    /// <summary>Tradução de texto escrito; sem motor que traduza texto (ou com erro), vai sem tradução.</summary>
    private async Task<string?> TranslateWrittenAsync(string text, CaptionOwner author)
    {
        if (_translation is not { CanTranslateText: true } translation)
        {
            return null;
        }
        try
        {
            return await translation.TranslateTextAsync(text, author, CancellationToken.None);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task SendAppAsync(AppMessage message)
    {
        if (_module is not null && Stage == CallStage.Connected)
        {
            try
            {
                await _module.InvokeAsync<bool>("sendApp", AppMessages.Serialize(message));
            }
            catch (JSException)
            {
                // Canal fechando: a mensagem se perde junto com a chamada.
            }
        }
    }

    /// <summary>Anfitrião: espera a resposta chegar pelo encontro automático.</summary>
    private void ListenForAnswer(CallRendezvous rendezvous, DateTimeOffset createdAt)
    {
        StopRendezvous();
        if (!relay.Enabled)
        {
            AutoConnect = AutoConnect.Off;
            return;
        }
        var stop = new CancellationTokenSource();
        _rendezvous = stop;
        AutoConnect = AutoConnect.Listening;
        var attempt = _attempt;
        // Tolerância para relógios fora de sincronia: mensagens um pouco "antigas" também valem.
        _ = relay.ListenAsync(rendezvous.Topic, createdAt - CallCodes.ClockTolerance, body =>
            rendezvous.Open(body) is { Type: RendezvousMessage.Answer } message
                ? ui.InvokeAsync(() => _ = AcceptRelayedAnswerAsync(message.Code, attempt))
                : Task.CompletedTask,
            stop.Token);
    }

    private async Task AcceptRelayedAnswerAsync(string code, int attempt)
    {
        if (attempt != _attempt || Stage != CallStage.WaitingAnswer)
        {
            return;
        }
        try
        {
            await AcceptAnswerAsync(code);
        }
        catch (CallCodeException)
        {
            // Resposta velha, de outro convite ou adulterada: o convite continua esperando.
        }
        catch (JSException)
        {
            End("Não foi possível preparar a conexão neste computador.");
            Notify();
        }
    }

    /// <summary>Convidado: manda a resposta pelo encontro automático; o código fica de reserva.</summary>
    private void PublishAnswer(CallRendezvous rendezvous, string answer)
    {
        StopRendezvous();
        if (!relay.Enabled)
        {
            AutoConnect = AutoConnect.Off;
            return;
        }
        var stop = new CancellationTokenSource();
        _rendezvous = stop;
        AutoConnect = AutoConnect.Sending;
        var attempt = _attempt;
        var message = rendezvous.Seal(new RendezvousMessage(RendezvousMessage.Answer, answer));
        _ = Task.Run(async () =>
        {
            bool sent;
            try
            {
                sent = await relay.PublishAsync(rendezvous.Topic, message, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await ui.InvokeAsync(() =>
            {
                if (attempt == _attempt && Stage == CallStage.Connecting)
                {
                    AutoConnect = sent ? AutoConnect.Sent : AutoConnect.Unavailable;
                    Notify();
                }
            });
        });
    }

    private void StopRendezvous()
    {
        _rendezvous?.Cancel();
        _rendezvous?.Dispose();
        _rendezvous = null;
    }

    private void StartOpeningDeadline(TimeSpan timeout, string reason)
    {
        StopOpeningDeadline();
        var attempt = _attempt;
        _openingDeadline = time.CreateTimer(_ => _ = ui.InvokeAsync(() =>
        {
            if (attempt == _attempt && Stage == CallStage.Connecting)
            {
                End(reason);
                Notify();
            }
        }), null, timeout, Timeout.InfiniteTimeSpan);
    }

    private void StopOpeningDeadline()
    {
        _openingDeadline?.Dispose();
        _openingDeadline = null;
    }

    private async Task StopTranslationAsync()
    {
        if (_translation is { } translation)
        {
            _translation = null;
            await translation.DisposeAsync();
        }
    }

    private async Task ResetAsync()
    {
        _attempt++;
        StopRendezvous();
        StopOpeningDeadline();
        AutoConnect = AutoConnect.Off;
        await StopTranslationAsync();
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("hangup");
        }
        _peerHello = null;
        _mediaStarted = false;
        _lastPartialSent.Clear();
        _writtenReceived.Clear();
        _pendingCorrections.Clear();
        EngineStats.Clear();
        EnginesProblem = null;
        _engineChoice = null;
        _translationGeneration = 0;
        Captions.Clear();
        Translator = TranslatorSide.None;
        TranslationNotice = null;
        _pendingInvite = null;
        _remoteSdp = null;
        Role = null;
        Peer = null;
        InviteExpiresAt = null;
        SecurityCode = null;
        RoundTripMs = null;
        Route = null;
        Problem = null;
        SharingScreen = false;
        RemoteScreen = false;
        ScreenStats = null;
        Floating = false;
        Noise = null;
        Audio = Audio with { RemoteScreenHasAudio = false, SharingHasAudio = false };
        Stage = CallStage.Idle;
        titleBar.SetPeer(null, null);
        titleBar.SetStatus(null);
    }

    private void End(string reason)
    {
        _attempt++;
        StopRendezvous();
        StopOpeningDeadline();
        _ = StopTranslationAsync();
        Stage = CallStage.Ended;
        Problem = reason;
        Floating = false;
        _pendingInvite = null;
        titleBar.SetStatus(null);
        _ = _module?.InvokeVoidAsync("hangup");
    }

    /// <summary>Retransmissão no formato do RTCIceServer do WebRTC, ou <c>null</c>.</summary>
    public static object? RelayFor(ConnectionSettings settings) => settings.HasRelay
        ? new { urls = settings.TurnUrl, username = settings.TurnUser, credential = settings.TurnPassword }
        : null;

    private static string ModeName(ScreenMode mode) => mode == ScreenMode.Motion ? "motion" : "detail";

    private void Notify() => Changed?.Invoke();
}
