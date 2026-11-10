using DinoTranslator.Server.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace DinoTranslator.Server.Infrastructure;

/// <summary>
/// Várias instâncias do servidor atrás do proxy, sem estado preso a nenhuma delas: o banco é o
/// PostgreSQL e, com <c>Redis:Configuration</c>, o Redis guarda o cache compartilhado e leva as
/// mensagens em tempo real de uma instância para as outras (SignalR). Sem Redis, tudo funciona
/// numa instância só.
/// </summary>
public static class Scaling
{
    public const string ReadyTag = "pronto";

    public static bool HasRedis(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Redis:Configuration"]);

    public static IServiceCollection AddScaling(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 256 * 1024;
            options.DefaultEntryOptions = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };
        });

        var health = services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("banco", tags: [ReadyTag]);

        if (HasRedis(configuration))
        {
            var redis = ConfigurationOptions.Parse(configuration["Redis:Configuration"]!);
            redis.AbortOnConnectFail = false;
            redis.ClientName = "dinotranslator";
            // Uma conexão só, multiplexada, para o cache e a presença.
            var multiplexer = new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(redis));
            services.AddSingleton(_ => multiplexer.Value);
            services.AddStackExchangeRedisCache(options =>
            {
                options.InstanceName = "dino:cache:";
                options.ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer.Value);
            });
            health.AddCheck<RedisHealthCheck>("redis", tags: [ReadyTag]);
        }
        return services;
    }

    /// <summary>Com Redis, as mensagens do SignalR chegam aos clientes de todas as instâncias.</summary>
    public static ISignalRServerBuilder AddScaleOut(this ISignalRServerBuilder signalR, IConfiguration configuration) =>
        HasRedis(configuration)
            ? signalR.AddStackExchangeRedis(configuration["Redis:Configuration"]!, options =>
            {
                options.Configuration.ChannelPrefix = RedisChannel.Literal("dino");
                options.Configuration.AbortOnConnectFail = false;
            })
            : signalR;

    /// <summary>/health: o processo responde. /health/pronto: banco e Redis também (o proxy só manda tráfego assim).</summary>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/pronto", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        return app;
    }

    private sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Sem banco.");
    }

    private sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await redis.GetDatabase().PingAsync();
                return HealthCheckResult.Healthy();
            }
            catch (RedisException)
            {
                return HealthCheckResult.Unhealthy("Sem Redis.");
            }
        }
    }
}
