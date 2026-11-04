using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Banco SQLite em <c>ConnectionStrings:Database</c>; sem configuração, usa
    /// <c>data/dinotranslator.db</c> dentro da pasta do servidor.
    /// </summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services) =>
        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            options.UseSqlite(ResolveConnectionString(configuration, environment));
        });

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static string ResolveConnectionString(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration.GetConnectionString("Database");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var folder = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(folder);
        return $"Data Source={Path.Combine(folder, "dinotranslator.db")}";
    }
}
