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
            .Validate(o => o.MaxUsers is >= 1 and <= 2, "Auth:MaxUsers deve ser 1 ou 2.")
            .ValidateOnStart();

        // PBKDF2-HMAC-SHA512 com 100 mil iterações (formato V3 do ASP.NET Core Identity).
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
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
                    // Senha trocada ou conta removida invalidam na hora os tokens já emitidos.
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        var valid = principal?.GetUserId() is { } userId
                            && Guid.TryParse(principal.FindFirst(AuthClaims.SecurityStamp)?.Value, out var stamp)
                            && await context.HttpContext.RequestServices.GetRequiredService<AppDbContext>()
                                .Users.AnyAsync(u => u.Id == userId && u.SecurityStamp == stamp, context.HttpContext.RequestAborted);
                        if (!valid)
                        {
                            context.Fail("Sessão inválida.");
                        }
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }
}
