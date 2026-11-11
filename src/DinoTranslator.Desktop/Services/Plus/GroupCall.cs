using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DinoTranslator.Desktop.Services.Audio;
using DinoTranslator.Desktop.Services.Translation;
using DinoTranslator.Shared;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DinoTranslator.Desktop.Services.Plus;

public sealed record GroupParticipant(string Id, string Name, string Language, bool Local, bool Speaking, bool Microphone, bool Camera, bool Screen);

/// <summary>Legenda de alguém da sala, já no idioma de quem lê.</summary>
public sealed record GroupCaption(string ParticipantId, string Name, int Segment, string Text, bool Final, DateTimeOffset At);

/// <summary>
/// Chamada em grupo de um canal de voz (ou da conversa direta), pelo LiveKit do servidor. Fica
/// ligada enquanto a pessoa navega por outros canais, como no Discord. As legendas: este
/// computador reconhece a própria fala com o motor configurado e manda, pelo canal de dados da
/// sala, o original e as traduções para os idiomas de quem está ouvindo.
/// </summary>
public sealed class GroupCall(IJSRuntime js, PlusApi api, PlusState state, SpeechEngineFactory engines, TranslationSettingsStore translation, TimeProvider time)
    : IAsyncDisposable
{
    private const int MaxCaptionText = 1500;
    private static readonly TimeSpan PartialEvery = TimeSpan.FromMilliseconds(400);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private IJSObjectReference? _module;
    private DotNetObjectReference<GroupCall>? _self;
    private ISpeechTranslationEngine? _engine;
    private ISpeechTranslationStream? _speech;
    private DateTimeOffset _lastPartial;
    private readonly List<GroupCaption> _captions = [];

    public event Action? Changed;

    public Guid? ChannelId { get; private set; }

    public string? ChannelName { get; private set; }

    public bool Joining { get; private set; }

    public string? Problem { get; private set; }

    public IReadOnlyList<GroupParticipant> Participants { get; private set; } = [];

    public bool Microphone { get; private set; } = true;

    public bool Deafened { get; private set; }

    public bool Camera { get; private set; }

    public bool Screen { get; private set; }

    public bool CaptionsOn => _speech is not null;

    public NoiseState? Noise { get; private set; }

    public IReadOnlyList<GroupCaption> Captions
    {
        get
        {
            lock (_captions)
            {
                return [.. _captions];
            }
        }
    }

    private string MyLanguage => state.Me?.Language ?? Languages.PortugueseBrazil;

    public async Task JoinAsync(ChannelInfo channel, string name, ElementReference grid)
    {
        if (Joining || ChannelId == channel.Id)
        {
            return;
        }
        await LeaveAsync();
        Joining = true;
        Problem = null;
        Notify();
        try
        {
            var join = await api.JoinVoiceAsync(channel.Id);
            if (join.Value is not { } permission)
            {
                Problem = join.Error;
                return;
            }
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/group-call.js");
            _self ??= DotNetObjectReference.Create(this);
            Participants = await _module.InvokeAsync<List<GroupParticipant>>("join", _self, permission.Url, permission.Token, grid);
            ChannelId = channel.Id;
            ChannelName = name;
            Microphone = true;
            Deafened = Camera = Screen = false;
            await RefreshNoiseAsync();
            await StartCaptionsAsync();
        }
        catch (JSException ex)
        {
            Problem = ex.Message.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                ? "Sem permissão para usar o microfone."
                : "Não deu para entrar na chamada. Confira a internet e tente de novo.";
            await LeaveAsync();
        }
        finally
        {
            Joining = false;
            Notify();
        }
    }

    public async Task BindGridAsync(ElementReference grid)
    {
        if (_module is not null && ChannelId is not null)
        {
            await _module.InvokeVoidAsync("bindGrid", grid);
        }
    }

    public async Task LeaveAsync()
    {
        if (_speech is { } speech)
        {
            _speech = null;
            await speech.DisposeAsync();
        }
        if (_module is not null && ChannelId is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("leave");
            }
            catch (JSDisconnectedException)
            {
                // A janela fechou.
            }
        }
        ChannelId = null;
        ChannelName = null;
        Participants = [];
        Noise = null;
        lock (_captions)
        {
            _captions.Clear();
        }
        Notify();
    }

    public async Task ToggleMicrophoneAsync()
    {
        Microphone = !Microphone;
        await Invoke("setMicrophone", Microphone);
    }

    public async Task ToggleDeafenAsync()
    {
        Deafened = !Deafened;
        await Invoke("setDeafened", Deafened);
        if (Deafened && Microphone)
        {
            await ToggleMicrophoneAsync();
        }
        Notify();
    }

    public async Task ToggleCameraAsync()
    {
        try
        {
            Camera = _module is not null && await _module.InvokeAsync<bool>("setCamera", !Camera);
        }
        catch (JSException)
        {
            Problem = "A câmera não abriu (em uso por outro programa ou bloqueada).";
        }
        Notify();
    }

    public async Task ToggleScreenAsync()
    {
        try
        {
            Screen = _module is not null && await _module.InvokeAsync<bool>("setScreen", !Screen);
        }
        catch (JSException)
        {
            // A pessoa cancelou a escolha da tela.
            Screen = false;
        }
        Notify();
    }

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
            // O painel mostra o que ficou valendo.
        }
        await RefreshNoiseAsync();
    }

    public Task BindNoiseMeterAsync(ElementReference? fill, ElementReference? mark) =>
        _module is null ? Task.CompletedTask : _module.InvokeVoidAsync("bindNoiseMeter", fill, mark).AsTask();

    // ---- Legendas -------------------------------------------------------------------

    private async Task StartCaptionsAsync()
    {
        // Serviço pago: só motores com licença comercial (MarianMT local ou o provedor de nuvem).
        if (_module is null || engines.CreateForPlus(translation.Current) is not { } engine)
        {
            return;
        }
        try
        {
            var stream = await engine.StartAsync(new SpeechStreamOptions(MyLanguage, MyLanguage, []), CancellationToken.None);
            stream.Updated += update => _ = PublishAsync(update);
            _engine = engine;
            _speech = stream;
            await _module.InvokeVoidAsync("enableCaptions");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JSException or Translation.Local.ModelDownloadException)
        {
            // Sem motor: a chamada segue sem as legendas desta pessoa.
        }
    }

    [JSInvokable]
    public void OnSpeechAudio(byte[] pcm)
    {
        if (pcm.Length is > 0 and <= 64 * 1024 && Microphone)
        {
            _speech?.Write(pcm);
        }
    }

    private sealed record CaptionWire(int S, bool F, string L, string T, Dictionary<string, string>? Tr);

    /// <summary>Manda a própria fala: provisória só no original (até 2,5 por segundo); final com as traduções.</summary>
    private async Task PublishAsync(SpeechUpdate update)
    {
        var text = LiveTranslation.Clean(update.SourceText);
        if (_module is null || text.Length == 0)
        {
            return;
        }
        text = text.Length > MaxCaptionText ? text[..MaxCaptionText] : text;
        AddCaption(new GroupCaption("eu", state.Me?.DisplayName ?? "Você", update.Segment, text, update.Final, time.GetUtcNow()));
        Dictionary<string, string>? translations = null;
        if (update.Final)
        {
            translations = [];
            var targets = Participants.Where(p => !p.Local && Languages.IsSupported(p.Language) && p.Language != MyLanguage).Select(p => p.Language).Distinct();
            foreach (var target in targets)
            {
                try
                {
                    if (_engine is { CanTranslateText: true } engine && await engine.TranslateTextAsync(text, MyLanguage, target, CancellationToken.None) is { Length: > 0 } translated)
                    {
                        var clean = LiveTranslation.Clean(translated);
                        translations[target] = clean.Length > MaxCaptionText ? clean[..MaxCaptionText] : clean;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
                {
                    // Sem tradução para esse idioma desta vez: vai o original.
                }
            }
        }
        else
        {
            var now = time.GetUtcNow();
            if (now - _lastPartial < PartialEvery)
            {
                return;
            }
            _lastPartial = now;
        }
        try
        {
            await _module.InvokeVoidAsync("sendCaption", JsonSerializer.Serialize(new CaptionWire(update.Segment, update.Final, MyLanguage, text, translations), Json));
        }
        catch (JSException)
        {
            // Saiu da sala no meio do envio.
        }
    }

    /// <summary>Legenda de outra pessoa: usa a tradução para o meu idioma, se veio; senão, o original.</summary>
    [JSInvokable]
    public void OnCaption(string participantId, string name, string json)
    {
        CaptionWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<CaptionWire>(json, Json);
        }
        catch (JsonException)
        {
            return;
        }
        if (wire is null || wire.T is not { Length: > 0 and <= MaxCaptionText } || participantId.Length > 64 || name.Length > 64)
        {
            return;
        }
        var text = wire.L == MyLanguage ? wire.T : wire.Tr?.GetValueOrDefault(MyLanguage) is { Length: > 0 and <= MaxCaptionText } mine ? mine : wire.T;
        AddCaption(new GroupCaption(participantId, name, wire.S, LiveTranslation.Clean(text), wire.F, time.GetUtcNow()));
    }

    private void AddCaption(GroupCaption caption)
    {
        lock (_captions)
        {
            var index = _captions.FindIndex(c => c.ParticipantId == caption.ParticipantId && c.Segment == caption.Segment);
            if (index >= 0)
            {
                _captions[index] = caption;
            }
            else
            {
                _captions.Add(caption);
            }
            if (_captions.Count > 60)
            {
                _captions.RemoveAt(0);
            }
        }
        Notify();
    }

    [JSInvokable]
    public void OnParticipants(List<GroupParticipant> participants)
    {
        Participants = participants.Take(200).ToList();
        Notify();
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

    [JSInvokable]
    public async Task OnDisconnected()
    {
        if (ChannelId is not null)
        {
            Problem = "A chamada caiu.";
            await LeaveAsync();
        }
    }

    private async Task Invoke(string method, params object?[] args)
    {
        if (_module is not null && ChannelId is not null)
        {
            await _module.InvokeVoidAsync(method, args);
        }
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await LeaveAsync();
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // A janela já fechou.
        }
        _self?.Dispose();
    }
}
