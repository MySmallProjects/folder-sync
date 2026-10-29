using Microsoft.EntityFrameworkCore;

namespace FolderSyncEr.Server.Data;

public class ServerDbContext : DbContext
{
    public ServerDbContext(DbContextOptions<ServerDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<FileRecord> Files => Set<FileRecord>();
    public DbSet<FileVersionRecord> FileVersions => Set<FileVersionRecord>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<ApiSetting> ApiSettings => Set<ApiSetting>();
    public DbSet<UserCounter> UserCounters => Set<UserCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(d => new { d.UserId, d.Id });
            entity.HasIndex(d => new { d.UserId, d.Id });
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(r => r.TokenHash).IsUnique();
            entity.HasIndex(r => new { r.UserId, r.DeviceId });
        });

        modelBuilder.Entity<FileRecord>(entity =>
        {
            entity.HasIndex(f => new { f.UserId, f.PathKey }).IsUnique();
            entity.HasIndex(f => new { f.UserId, f.ChangeSeq });
            entity.HasIndex(f => new { f.UserId, f.IsDeleted });
        });

        modelBuilder.Entity<FileVersionRecord>(entity =>
        {
            entity.HasIndex(v => new { v.FileId, v.Version }).IsUnique();
        });

        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.HasIndex(l => new { l.UserId, l.TimestampUtc });
        });
    }
}

