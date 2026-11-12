using DinoTranslator.Shared.Call;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DinoTranslator.Desktop.Services.Plus;

/// <summary>Quem leva os sinais da chamada pela conta: o tempo real da versão paga.</summary>
public interface ICallSignaling
{
    event Action<CallSignalInfo>? CallSignalReceived;

    /// <summary>Manda o sinal; diz se saiu e se a outra pessoa está com o app aberto.</summary>
    Task<SignalResult> SendCallSignalAsync(Guid channelId, string callId, CallSignal signal, string? code);

    /// <summary>Nome (da conta) da outra pessoa da conversa direta.</summary>
    string? PeerName(Guid channelId);
}

public readonly record struct SignalResult(bool Sent, bool Online, string? Problem = null);

public enum DirectCallPhase
{
    /// <summary>Alguém está ligando para cá.</summary>
    Incoming,

    /// <summary>Daqui: tocando lá.</summary>
    Ringing,

    /// <summary>Atendida: os dois computadores estão se conectando.</summary>
    Connecting,

    /// <summary>Acabou sem conectar (recusou, não atendeu, chamada perdida...): fica o aviso.</summary>
    Ended,
}

/// <summary>A chamada pela conta até virar a tela da chamada (ou o aviso, se não conectou).</summary>
public sealed record DirectCallState(DirectCallPhase Phase, Guid ChannelId, string Name, string CallId, bool Outgoing, string? Message = null);

/// <summary>
/// Chamada 1:1 pela conta, a partir da conversa direta: quem liga gera o convite da chamada e o
/// manda pelo servidor; quem recebe vê o aviso em qualquer tela, atende e devolve a resposta pelo
/// mesmo caminho. Daí em diante é a mesma chamada da versão grátis — direta, cifrada de ponta a
/// ponta, com a conexão garantida de reserva — e a tela da chamada abre quando conecta.
/// </summary>
public sealed class DirectCalls : IDisposable
{
    /// <summary>Quanto tempo toca antes de desistir.</summary>
    public static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Quem recebe para de tocar um pouco depois, mesmo se o aviso de desistência não chegar.</summary>
    public static readonly TimeSpan IncomingTimeout = RingTimeout + TimeSpan.FromSeconds(15);

    private readonly ICallSignaling _signaling;
    private readonly CallSession _call;
    private readonly NavigationManager _navigation;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private string? _invite;
    private ITimer? _timer;

    public DirectCalls(ICallSignaling signaling, CallSession call, NavigationManager navigation, IUiDispatcher ui, TimeProvider time)
    {
        _signaling = signaling;
        _call = call;
        _navigation = navigation;
        _ui = ui;
        _time = time;
        _signaling.CallSignalReceived += OnSignal;
        _call.Changed += OnCallChanged;
    }

    public event Action? Changed;

    public DirectCallState? State { get; private set; }

    /// <summary>Já há uma chamada (pela conta ou por código) em andamento.</summary>
    public bool Busy =>
        State is { Phase: not DirectCallPhase.Ended }
        || _call.Stage is CallStage.WaitingAnswer or CallStage.Connecting or CallStage.Connected;

    /// <summary>Liga para a outra pessoa da conversa direta.</summary>
    public async Task CallAsync(Guid channelId)
    {
        if (Busy)
        {
            return;
        }
        var name = _signaling.PeerName(channelId) ?? "a outra pessoa";
        string invite;
        try
        {
            invite = await _call.CreateInviteAsync(CallTransport.Account);
        }
        catch (Exception ex) when (ex is CallCodeException or JSException)
        {
            Show(new DirectCallState(DirectCallPhase.Ended, channelId, name, string.Empty, Outgoing: true, Problem(ex)));
            return;
        }
        var callId = _call.CallId!;
        var ringing = new DirectCallState(DirectCallPhase.Ringing, channelId, name, callId, Outgoing: true);
        Show(ringing);
        var sent = await _signaling.SendCallSignalAsync(channelId, callId, CallSignal.Ring, invite);
        if (State != ringing)
        {
            // Desistiu (ou a resposta já chegou) enquanto o sinal saía.
            return;
        }
        if (!sent.Sent || !sent.Online)
        {
            await _call.HangUpAsync();
            Show(ringing with
            {
                Phase = DirectCallPhase.Ended,
                Message = sent.Sent ? $"{name} não está com o app aberto agora." : sent.Problem ?? "Sem conexão com o servidor agora.",
            });
            return;
        }
        StartTimer(RingTimeout, () => NoAnswerAsync(ringing));
    }

    /// <summary>Atende a chamada que está tocando.</summary>
    public async Task AnswerAsync()
    {
        if (State is not { Phase: DirectCallPhase.Incoming } incoming || _invite is not { } invite)
        {
            return;
        }
        StopTimer();
        _invite = null;
        string answer;
        try
        {
            answer = await _call.AcceptInviteAsync(invite, CallTransport.Account);
        }
        catch (Exception ex) when (ex is CallCodeException or JSException)
        {
            await _signaling.SendCallSignalAsync(incoming.ChannelId, incoming.CallId, CallSignal.Decline, null);
            Show(incoming with { Phase = DirectCallPhase.Ended, Message = Problem(ex) });
            return;
        }
        var connecting = incoming with { Phase = DirectCallPhase.Connecting };
        Show(connecting);
        var sent = await _signaling.SendCallSignalAsync(incoming.ChannelId, incoming.CallId, CallSignal.Answer, answer);
        if (!sent.Sent && State == connecting)
        {
            await _call.HangUpAsync();
            Show(connecting with { Phase = DirectCallPhase.Ended, Message = sent.Problem ?? "Sem conexão com o servidor agora." });
        }
    }

    /// <summary>Recusa a chamada que está tocando.</summary>
    public async Task DeclineAsync()
    {
        if (State is not { Phase: DirectCallPhase.Incoming } incoming)
        {
            return;
        }
        StopTimer();
        _invite = null;
        Show(null);
        await _signaling.SendCallSignalAsync(incoming.ChannelId, incoming.CallId, CallSignal.Decline, null);
    }

    /// <summary>Desiste: quem liga, enquanto toca; qualquer um dos dois, enquanto conecta.</summary>
    public async Task CancelAsync()
    {
        if (State is not { Phase: DirectCallPhase.Ringing or DirectCallPhase.Connecting } state)
        {
            return;
        }
        StopTimer();
        Show(null);
        await _signaling.SendCallSignalAsync(state.ChannelId, state.CallId, CallSignal.Cancel, null);
        await _call.HangUpAsync();
    }

    /// <summary>Fecha o aviso de uma chamada que acabou sem conectar.</summary>
    public void Dismiss()
    {
        if (State is { Phase: DirectCallPhase.Ended })
        {
            Show(null);
        }
    }

    private void OnSignal(CallSignalInfo signal) => _ = _ui.InvokeAsync(() => _ = HandleAsync(signal));

    internal async Task HandleAsync(CallSignalInfo signal)
    {
        if (!CallSignalLimits.ValidCallId(signal.CallId))
        {
            return;
        }
        var current = State is { } state && state.ChannelId == signal.ChannelId && state.CallId == signal.CallId ? state : null;
        switch (signal.Signal)
        {
            case CallSignal.Ring:
                await RingAsync(signal);
                break;
            case CallSignal.Answer when current is { Phase: DirectCallPhase.Ringing, Outgoing: true } && signal.Code is { } answer:
                StopTimer();
                var connecting = current with { Phase = DirectCallPhase.Connecting };
                Show(connecting);
                try
                {
                    await _call.AcceptAnswerAsync(answer);
                }
                catch (Exception ex) when (ex is CallCodeException or JSException)
                {
                    await _signaling.SendCallSignalAsync(current.ChannelId, current.CallId, CallSignal.Cancel, null);
                    await _call.HangUpAsync();
                    Show(connecting with { Phase = DirectCallPhase.Ended, Message = Problem(ex) });
                }
                break;
            case CallSignal.Decline when current is { Phase: DirectCallPhase.Ringing, Outgoing: true }:
                await EndAsync(current, $"{current.Name} recusou a chamada.");
                break;
            case CallSignal.Busy when current is { Phase: DirectCallPhase.Ringing, Outgoing: true }:
                await EndAsync(current, $"{current.Name} está em outra chamada.");
                break;
            case CallSignal.Cancel when current is { Phase: DirectCallPhase.Incoming }:
                StopTimer();
                _invite = null;
                Show(current with { Phase = DirectCallPhase.Ended, Message = $"Chamada perdida de {current.Name}." });
                break;
            case CallSignal.Cancel when current is { Phase: DirectCallPhase.Connecting }:
                await EndAsync(current, $"{current.Name} desistiu da chamada.");
                break;
            case CallSignal.Handled when current is { Phase: DirectCallPhase.Incoming }:
                // Atendida ou recusada em outro aparelho desta conta.
                StopTimer();
                _invite = null;
                Show(null);
                break;
        }
    }

    private async Task RingAsync(CallSignalInfo signal)
    {
        if (signal.Code is not { } code)
        {
            return;
        }
        CallCode invite;
        try
        {
            invite = CallCodes.Decode(code, CallCodeKind.Invite, _time.GetUtcNow());
        }
        catch (CallCodeException)
        {
            return;
        }
        if (invite.CallId != signal.CallId || State is { Phase: DirectCallPhase.Incoming } same && same.CallId == signal.CallId)
        {
            return;
        }
        if (Busy)
        {
            await _signaling.SendCallSignalAsync(signal.ChannelId, signal.CallId, CallSignal.Busy, null);
            return;
        }
        // O nome da conta (vem do servidor) vale mais que o do perfil escrito no convite.
        var incoming = new DirectCallState(DirectCallPhase.Incoming, signal.ChannelId,
            _signaling.PeerName(signal.ChannelId) ?? invite.Profile.Name, signal.CallId, Outgoing: false);
        _invite = code;
        Show(incoming);
        StartTimer(IncomingTimeout, () =>
        {
            if (State == incoming)
            {
                _invite = null;
                Show(incoming with { Phase = DirectCallPhase.Ended, Message = $"Chamada perdida de {incoming.Name}." });
            }
            return Task.CompletedTask;
        });
    }

    private async Task NoAnswerAsync(DirectCallState ringing)
    {
        if (State != ringing)
        {
            return;
        }
        await _signaling.SendCallSignalAsync(ringing.ChannelId, ringing.CallId, CallSignal.Cancel, null);
        await EndAsync(ringing, $"{ringing.Name} não atendeu.");
    }

    private async Task EndAsync(DirectCallState state, string message)
    {
        StopTimer();
        Show(state with { Phase = DirectCallPhase.Ended, Message = message });
        await _call.HangUpAsync();
    }

    /// <summary>Conectou: abre a tela da chamada. Não conectou: fica o aviso com o motivo.</summary>
    private void OnCallChanged()
    {
        if (State is not { Phase: DirectCallPhase.Connecting } state || _call.CallId != state.CallId)
        {
            return;
        }
        switch (_call.Stage)
        {
            case CallStage.Connected:
                Show(null);
                _ = _ui.InvokeAsync(() => _navigation.NavigateTo("/chamada"));
                break;
            case CallStage.Ended:
                Show(state with { Phase = DirectCallPhase.Ended, Message = _call.Problem ?? "A chamada não conectou." });
                break;
        }
    }

    private static string Problem(Exception ex) =>
        ex is CallCodeException ? ex.Message : "Não foi possível preparar a conexão neste computador.";

    private void Show(DirectCallState? state)
    {
        State = state;
        Changed?.Invoke();
    }

    private void StartTimer(TimeSpan due, Func<Task> action)
    {
        StopTimer();
        _timer = _time.CreateTimer(_ => _ = _ui.InvokeAsync(() => _ = action()), null, due, Timeout.InfiniteTimeSpan);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        StopTimer();
        _signaling.CallSignalReceived -= OnSignal;
        _call.Changed -= OnCallChanged;
    }
}
