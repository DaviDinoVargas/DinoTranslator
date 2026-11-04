using Microsoft.Extensions.DependencyInjection;

namespace DinoTranslator.Desktop.Services;

public static class DesktopServices
{
    public static IServiceCollection AddDesktopServices(this IServiceCollection services, DesktopSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
