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
    IUiDispatcher ui,
    TimeProvider time) : IAsyncDisposable
{
    /// <summary>Intervalo mínimo entre legendas provisórias enviadas ao outro lado; finais vão sempre.</summary>
    private static readonly TimeSpan PartialCaptionInterval = TimeSpan.FromMilliseconds(150);

    private const int MaxSpeechChunkBytes = 64 * 1024;

    private IJSObjectReference? _module;
    private DotNetObjectReference<CallSession>? _self;
    private CallCode? _pendingInvite;
    private string? _remoteSdp;
    private HelloMessage? _peerHello;
    private bool _mediaStarted;
    private LiveTranslation? _translation;
    private readonly Dictionary<CaptionOwner, DateTimeOffset> _lastPartialSent = [];

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

    public bool SharingScreen { get; private set; }

    public bool RemoteScreen { get; private set; }

    public ScreenMode ScreenMode { get; private set; } = ScreenMode.Detail;

    /// <summary>Ex.: "2560×1440 · 30 fps · AV1".</summary>
    public string? ScreenStats { get; private set; }

    public CaptionBoard Captions { get; } = new(time);

    public TranslatorSide Translator { get; private set; }

    /// <summary>Aviso sobre a tradução para mostrar na chamada (desligada, erro do motor etc.).</summary>
    public string? TranslationNotice { get; private set; }

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
        Notify();
        return text;
    }

    /// <summary>Anfitrião: lê a resposta do convidado e conecta.</summary>
    public async Task AcceptAnswerAsync(string text)
    {
        if (Stage != CallStage.WaitingAnswer || _pendingInvite is not { } invite || _module is null)
        {
            throw new CallCodeException("Crie um convite antes de colar a resposta.");
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
        Peer = answer.Profile;
        _remoteSdp = answer.Sdp;
        Stage = CallStage.Connecting;
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
        Notify();
        return answer;
    }

    public async Task StartMediaAsync(ElementReference localVideo, ElementReference remoteVideo, bool highFidelity)
    {
        if (_module is not null && Stage == CallStage.Connected)
        {
            await _module.InvokeVoidAsync("startMedia", localVideo, remoteVideo, highFidelity);
            _mediaStarted = true;
            await MaybeStartTranslationAsync();
        }
    }

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

    public async Task BindRemoteScreenAsync(ElementReference video)
    {
        if (_module is not null && Stage == CallStage.Connected)
        {
            await _module.InvokeVoidAsync("bindRemoteScreen", video);
        }
    }

    public async Task HangUpAsync()
    {
        if (_module is not null && Stage == CallStage.Connected)
        {
            await _module.InvokeAsync<bool>("sendApp", AppMessages.Serialize(new ByeMessage()));
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
        titleBar.SetPeer(Peer?.Name, Peer?.TimeZone);
        Notify();
        await SendAppAsync(new HelloMessage(translationSettings.Current.IsConfigured));
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
            case "failed":
                End("A conexão caiu. Para voltar, criem um novo convite.");
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
                Translator = TranslatorChoice.Decide(role, translationSettings.Current.IsConfigured, hello.CanTranslate);
                TranslationNotice = Translator == TranslatorSide.None
                    ? "Tradução desligada: nenhum dos dois configurou o motor de tradução (Início › Tradução)."
                    : null;
                Notify();
                await MaybeStartTranslationAsync();
                break;
            // Legendas só são aceitas de quem foi escolhido para traduzir.
            case CaptionMessage caption when Translator == TranslatorSide.There:
                if (Captions.Apply(CaptionBoard.FromMessage(caption, time.GetUtcNow())))
                {
                    Notify();
                }
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
        Problem = "Não foi possível ajustar o áudio ou o vídeo da chamada.";
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
        _self?.Dispose();
    }

    private Profile RequireProfile() =>
        profiles.Current ?? throw new CallCodeException("Preencha o seu perfil antes de chamar.");

    private async Task<IJSObjectReference> StartPeerAsync(bool polite)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/call.js");
        _self ??= DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("create", _self, polite);
        return _module;
    }

    /// <summary>Liga a tradução quando este computador foi escolhido e a mídia já começou.</summary>
    private async Task MaybeStartTranslationAsync()
    {
        if (Translator != TranslatorSide.Here || _translation is not null || !_mediaStarted
            || _module is null || _self is null || Peer is not { } peer || profiles.Current is not { } me
            || SpeechTranslationEngines.Create(translationSettings.Current, time) is not { } engine)
        {
            return;
        }

        var translation = new LiveTranslation(engine, me, peer, time);
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
        catch (Exception)
        {
            _translation = null;
            await translation.DisposeAsync();
            TranslationNotice = "Não foi possível ligar a tradução. A chamada continua, sem legendas.";
            Notify();
        }
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
        await StopTranslationAsync();
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("hangup");
        }
        _peerHello = null;
        _mediaStarted = false;
        _lastPartialSent.Clear();
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
        Stage = CallStage.Idle;
        titleBar.SetPeer(null, null);
        titleBar.SetStatus(null);
    }

    private void End(string reason)
    {
        _ = StopTranslationAsync();
        Stage = CallStage.Ended;
        Problem = reason;
        _pendingInvite = null;
        titleBar.SetStatus(null);
        _ = _module?.InvokeVoidAsync("hangup");
    }

    private static string ModeName(ScreenMode mode) => mode == ScreenMode.Motion ? "motion" : "detail";

    private void Notify() => Changed?.Invoke();
}
