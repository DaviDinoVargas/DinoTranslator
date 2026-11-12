using System.Globalization;
using System.Threading.RateLimiting;
using DinoTranslator.Server.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DinoTranslator.Server.Auth;

public static class AuthSetup
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.Section)
            .Validate(o => o.MaxUsers >= 0, "Auth:MaxUsers não pode ser negativo (0 = sem limite).")
            .Validate(o => o.AccessTokenMinutes is >= 1 and <= 60, "Auth:AccessTokenMinutes deve ficar entre 1 e 60.")
            .Validate(o => string.IsNullOrEmpty(o.RegistrationCode) || o.RegistrationCode.Length >= 12, "Auth:RegistrationCode precisa ter pelo menos 12 caracteres.")
            .ValidateOnStart();

        // PBKDF2-HMAC-SHA512 (formato V3 do ASP.NET Core Identity) com as 210 mil iterações
        // recomendadas pela OWASP; senhas antigas são refeitas no próximo login.
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210_000);
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<SessionValidator>();
        services.AddScoped<UserAdministration>();
        return services;
    }

    public static IServiceCollection AddTokenAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.Section)
            .Validate(o => o.HasValidKey(), "Jwt:SigningKey precisa ser base64 com pelo menos 256 bits (use user-secrets ou a variável Jwt__SigningKey).")
            .ValidateOnStart();

        services.AddSingleton<DummyPasswordHash>();
        services.AddSingleton<TokenService>();
        services.AddScoped<AuthService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider>((bearer, jwt, time) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = jwt.Value.CreateSecurityKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    NameClaimType = AuthClaims.Name,
                    // Validade conferida com o mesmo relógio que emitiu o token.
                    LifetimeValidator = (notBefore, expires, _, _) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        return expires is { } exp && exp > now - ClockSkew && (notBefore is not { } nbf || nbf <= now + ClockSkew);
                    },
                };
                bearer.Events = new JwtBearerEvents
                {
                    // O token só é aceito no cabeçalho Authorization (o cliente SignalR .NET o envia
                    // também em WebSocket); nunca na query, onde acabaria em URLs e logs.
                    // Senha trocada ou conta removida invalidam na hora os tokens já emitidos.
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        var valid = principal?.GetUserId() is { } userId
                            && Guid.TryParse(principal.FindFirst(AuthClaims.SecurityStamp)?.Value, out var stamp)
                            && await context.HttpContext.RequestServices.GetRequiredService<SessionValidator>()
                                .IsValidAsync(userId, stamp, context.HttpContext.RequestAborted);
                        if (!valid)
                        {
                            context.Fail("Sessão inválida.");
                        }
                    },
                };
            });

        // Versão paga: o token diz se o Plus valia na emissão; o prazo é conferido de novo aqui.
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthClaims.PlusPolicy, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                PaidPlanValid(context.User, "plus", "pro")))
            .AddPolicy(AuthClaims.ProPolicy, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                PaidPlanValid(context.User, "pro")));
        return services;
    }

    /// <summary>O token diz qual plano valia na emissão; o prazo é conferido de novo aqui.</summary>
    private static bool PaidPlanValid(System.Security.Claims.ClaimsPrincipal user, params string[] plans)
    {
        if (!plans.Contains(user.FindFirst(AuthClaims.Plan)?.Value))
        {
            return false;
        }
        var until = user.FindFirst(AuthClaims.PlanUntil)?.Value;
        return until is null
            || (long.TryParse(until, CultureInfo.InvariantCulture, out var seconds)
                && DateTimeOffset.FromUnixTimeSeconds(seconds) > TimeProvider.System.GetUtcNow());
    }

    public const string LoginRateLimit = "login";
    public const string RefreshRateLimit = "refresh";
    public const string RegisterRateLimit = "register";

    /// <summary>Janela fixa de 1 min por IP, sem fila; o excesso recebe 429 com Retry-After.</summary>
    public static IServiceCollection AddAuthRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(LoginRateLimit, context => PerClientIp(context, o => o.LoginPermitsPerMinute));
            limiter.AddPolicy(RefreshRateLimit, context => PerClientIp(context, o => o.RefreshPermitsPerMinute));
            limiter.AddPolicy(RegisterRateLimit, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter("cadastro:" + (context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido"), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RegisterPermitsPerHour,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }

    private static RateLimitPartition<string> PerClientIp(HttpContext context, Func<AuthOptions, int> permits)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits(options),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }
}
