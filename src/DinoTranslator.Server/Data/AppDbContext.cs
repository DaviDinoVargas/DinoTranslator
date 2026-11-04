using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => u.UserName).IsUnique();
            user.Property(u => u.UserName).HasMaxLength(32);
            user.Property(u => u.DisplayName).HasMaxLength(64);
            user.Property(u => u.Language).HasMaxLength(8);
            user.Property(u => u.PasswordHash).HasMaxLength(512);
        });

        modelBuilder.Entity<RefreshToken>(token =>
        {
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.SessionId);
            token.Property(t => t.TokenHash).HasMaxLength(32);
            token.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
