using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;

namespace DinoTranslator.Server.Room;

/// <summary>
/// Limita as chamadas ao hub por conexão. A rajada cobre a troca de candidatos ICE
/// no início da chamada; acima disso a conexão está sendo usada de forma abusiva.
/// </summary>
public sealed class HubRateLimitFilter : IHubFilter
{
    private const string LimiterKey = "rate-limiter";

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var limiter = (RateLimiter)invocationContext.Context.Items[LimiterKey]!;
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            throw new HubException("Muitas mensagens. Aguarde um instante.");
        }
        return await next(invocationContext);
    }

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        context.Context.Items[LimiterKey] = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 200,
            TokensPerPeriod = 50,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
        return next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        try
        {
            await next(context, exception);
        }
        finally
        {
            if (context.Context.Items[LimiterKey] is RateLimiter limiter)
            {
                await limiter.DisposeAsync();
            }
        }
    }
}
