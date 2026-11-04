using DinoTranslator.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DinoTranslator.Server.Auth;

public static class AuthSetup
{
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
}
