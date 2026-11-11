using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// PostgreSQL em produção (<c>ConnectionStrings:Database</c> no formato "Host=…"), com as
    /// migrações do projeto. SQLite fica para desenvolvimento e testes, criado direto do modelo;
    /// sem configuração, usa <c>data/dinotranslator.db</c> dentro da pasta do servidor.
    /// </summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services) =>
        services.AddDbContextPool<AppDbContext>((provider, options) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            var connection = ResolveConnectionString(configuration, environment);
            if (IsPostgres(connection))
            {
                // Sem nova tentativa automática: as transações do login e do cadastro são explícitas.
                options.UseNpgsql(connection);
            }
            else
            {
                options.UseSqlite(connection);
            }
        });

    public static bool IsPostgres(string connectionString) =>
        connectionString.TrimStart().StartsWith("Host=", StringComparison.OrdinalIgnoreCase);

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsNpgsql())
        {
            // Várias instâncias sobem juntas: uma migra de cada vez (trava do próprio PostgreSQL,
            // solta sozinha se a instância cair); as outras esperam e acham tudo pronto.
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(7240513)", cancellationToken);
                await db.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(7240513)", CancellationToken.None);
                await db.Database.CloseConnectionAsync();
            }
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }
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
