using System.Threading.RateLimiting;
using DinoTranslator.Server.Auth;
using DinoTranslator.Server.Data;
using DinoTranslator.Server.Social;
using DinoTranslator.Shared.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Realtime;

/// <summary>
/// Conexão em tempo real do app na versão paga: entra nos grupos da pessoa para receber as
/// mensagens e avisos, marca presença e repassa "digitando". As mensagens em si são criadas pela
/// API (POST), que manda o evento por aqui.
/// </summary>
[Authorize(Policy = AuthClaims.PlusPolicy)]
public sealed class AppHub(AppDbContext db, IPresenceStore presence, Notifier notifier, SocialAccess access) : Hub<IAppClient>
{
    private const string RingLimiterKey = "ring-limiter";

    private Guid CurrentUserId => Context.User?.GetUserId() ?? throw new HubException("Não autenticado.");

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId;
        var groups = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync(Context.ConnectionAborted);
        foreach (var groupId in groups)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Notifier.GroupName(groupId), Context.ConnectionAborted);
        }
        if (await presence.ConnectedAsync(userId, Context.ConnectionId))
        {
            await notifier.PresenceAsync(userId, online: true);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = CurrentUserId;
        if (await presence.DisconnectedAsync(userId, Context.ConnectionId))
        {
            await notifier.PresenceAsync(userId, online: false);
        }
        if (Context.Items.Remove(RingLimiterKey, out var limiter) && limiter is RateLimiter ring)
        {
            await ring.DisposeAsync();
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// O app chama logo depois de conectar: o SignalR só atende depois de OnConnectedAsync, então a
    /// resposta garante que a conexão já está nos grupos e recebe os eventos. Só aí o app carrega
    /// as listas, e nenhuma mensagem cai no intervalo.
    /// </summary>
    public Task<bool> Ready() => Task.FromResult(true);

    /// <summary>
    /// Sinal da chamada 1:1 pela conta, só entre as duas pessoas da conversa direta e sem bloqueio
    /// entre elas. O servidor só repassa: o convite e a resposta levam a impressão digital da
    /// cifra da mídia, e o código de segurança na tela da chamada mostra se algo foi trocado no
    /// caminho. Devolve se a outra pessoa está com o app conectado agora.
    /// </summary>
    public async Task<bool> DirectCall(Guid channelId, string callId, CallSignal signal, string? code)
    {
        var userId = CurrentUserId;
        var carriesCode = signal is CallSignal.Ring or CallSignal.Answer;
        if (!Enum.IsDefined(signal) || signal == CallSignal.Handled || !CallSignalLimits.ValidCallId(callId)
            || (carriesCode ? code is not { Length: > 0 and <= CallSignalLimits.MaxCodeLength } : code is not null))
        {
            throw new HubException("Sinal de chamada inválido.");
        }
        if (signal == CallSignal.Ring && !RingAllowed())
        {
            throw new HubException("Muitas chamadas seguidas. Aguarde um instante.");
        }
        if (await access.ReadableAsync(channelId, userId, Context.ConnectionAborted) is not { } readable || !readable.Channel.IsDirect)
        {
            throw new HubException("Conversa não encontrada.");
        }
        var other = readable.Channel.OtherDirectUser(userId)!.Value;
        if (await access.BlockedAsync(userId, other, Context.ConnectionAborted))
        {
            throw new HubException("Não é possível ligar para esta pessoa.");
        }
        await Clients.User(other.ToString()).DirectCall(new CallSignalInfo(channelId, userId, callId, signal, code));
        if (signal is CallSignal.Answer or CallSignal.Decline or CallSignal.Busy)
        {
            // Os outros aparelhos de quem respondeu param de tocar.
            await Clients.User(userId.ToString()).DirectCall(new CallSignalInfo(channelId, other, callId, CallSignal.Handled, null));
        }
        return (await presence.OnlineAsync([other])).Contains(other);
    }

    /// <summary>Toques por conexão: uma rajada curta e depois um a cada 10 s.</summary>
    private bool RingAllowed()
    {
        if (!Context.Items.TryGetValue(RingLimiterKey, out var item) || item is not RateLimiter limiter)
        {
            limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = CallSignalLimits.RingBurst,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = CallSignalLimits.RingEvery,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
            Context.Items[RingLimiterKey] = limiter;
        }
        using var lease = limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    /// <summary>"Fulano está digitando" para quem pode ver o canal.</summary>
    public async Task Typing(Guid channelId)
    {
        var userId = CurrentUserId;
        if (await access.ReadableAsync(channelId, userId, Context.ConnectionAborted) is not { } readable)
        {
            throw new HubException("Canal não encontrado.");
        }
        var channel = readable.Channel;
        if (channel.IsDirect)
        {
            await Clients.User(channel.OtherDirectUser(userId)!.Value.ToString()).Typing(channelId, userId);
        }
        else
        {
            await Clients.OthersInGroup(Notifier.GroupName(channel.GroupId!.Value)).Typing(channelId, userId);
        }
    }
}
