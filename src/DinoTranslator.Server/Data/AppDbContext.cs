using Microsoft.EntityFrameworkCore;

namespace DinoTranslator.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<GroupInvite> GroupInvites => Set<GroupInvite>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    public DbSet<Block> Blocks => Set<Block>();

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

        modelBuilder.Entity<Group>(group =>
        {
            group.Property(g => g.Name).HasMaxLength(64);
            group.HasIndex(g => g.OwnerId);
        });

        modelBuilder.Entity<GroupMember>(member =>
        {
            member.HasKey(m => new { m.GroupId, m.UserId });
            member.HasIndex(m => m.UserId);
            member.HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Channel>(channel =>
        {
            channel.Property(c => c.Name).HasMaxLength(64);
            channel.HasIndex(c => c.GroupId);
            channel.HasIndex(c => new { c.DirectLowId, c.DirectHighId }).IsUnique();
            channel.HasIndex(c => c.DirectHighId);
            channel.HasOne<Group>().WithMany().HasForeignKey(c => c.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(message =>
        {
            // Id crescente gerado pelo banco: ordem de chegada e paginação por "antes de".
            message.Property(m => m.Id).ValueGeneratedOnAdd();
            message.Property(m => m.Content).HasMaxLength(4000);
            message.Property(m => m.Language).HasMaxLength(8);
            message.HasIndex(m => new { m.ChannelId, m.Id });
            message.HasIndex(m => m.AuthorId);
            message.HasOne<Channel>().WithMany().HasForeignKey(m => m.ChannelId).OnDelete(DeleteBehavior.Cascade);
            message.HasOne(m => m.Author).WithMany().HasForeignKey(m => m.AuthorId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GroupInvite>(invite =>
        {
            invite.HasKey(i => i.Code);
            invite.Property(i => i.Code).HasMaxLength(16);
            invite.HasIndex(i => i.GroupId);
            invite.HasOne<Group>().WithMany().HasForeignKey(i => i.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Friendship>(friendship =>
        {
            friendship.HasKey(f => new { f.LowId, f.HighId });
            friendship.HasIndex(f => f.HighId);
            friendship.HasOne<User>().WithMany().HasForeignKey(f => f.LowId).OnDelete(DeleteBehavior.Cascade);
            friendship.HasOne<User>().WithMany().HasForeignKey(f => f.HighId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Block>(block =>
        {
            block.HasKey(b => new { b.UserId, b.BlockedId });
            block.HasIndex(b => b.BlockedId);
            block.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
            block.HasOne<User>().WithMany().HasForeignKey(b => b.BlockedId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
