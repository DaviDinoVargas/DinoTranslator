using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DinoTranslator.Server.Data;

/// <summary>
/// Usada só pelas ferramentas do EF (dotnet ef migrations) em tempo de design: as migrações são
/// do PostgreSQL, o banco de produção. Nenhuma conexão é aberta para gerar uma migração.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=dinotranslator").Options);
}
