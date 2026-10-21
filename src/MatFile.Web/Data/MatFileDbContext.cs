using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MatFile.Web.Data;

/// <summary>Speichert Zeitstempel immer in UTC und liefert sie auch so wieder zurück.</summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}

public sealed class UtcNullableDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public UtcNullableDateTimeConverter()
        : base(v => v.HasValue ? v.Value.ToUniversalTime() : v,
               v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
    {
    }
}

public class MatFileDbContext : DbContext
{
    public MatFileDbContext(DbContextOptions<MatFileDbContext> options) : base(options)
    {
    }

    public DbSet<User> User => Set<User>();
    public DbSet<Role> Role => Set<Role>();
    public DbSet<UserRole> UserRole => Set<UserRole>();
    public DbSet<UserSession> UserSession => Set<UserSession>();
    public DbSet<StorageRoot> StorageRoot => Set<StorageRoot>();
    public DbSet<RootAccess> RootAccess => Set<RootAccess>();
    public DbSet<Bookmark> Bookmark => Set<Bookmark>();
    public DbSet<ExplorerTab> ExplorerTab => Set<ExplorerTab>();
    public DbSet<UserSetting> UserSetting => Set<UserSetting>();
    public DbSet<JobTask> JobTask => Set<JobTask>();
    public DbSet<ActivityLog> ActivityLog => Set<ActivityLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite kann DateTimeOffset nicht vergleichen - daher durchgehend DateTime in UTC.
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Tabellennamen in PascalCase, identisch zum Entitätsnamen.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            entity.SetTableName(entity.ClrType.Name);
        }

        model.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.ExternalSubject).HasMaxLength(256);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
        });

        model.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(256);
            e.Property(x => x.Permissions).HasConversion<int>();
        });

        model.Entity<UserRole>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
            e.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany(x => x.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany(x => x.Sessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.IdentitySessionId).HasMaxLength(256);
            e.HasIndex(x => x.IdentitySessionId);
        });

        model.Entity<StorageRoot>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.BasePath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.DisplayIcon).HasMaxLength(64);
            e.Property(x => x.StorageType).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Persistence).HasConversion<string>().HasMaxLength(32);
        });

        model.Entity<RootAccess>(e =>
        {
            e.HasIndex(x => new { x.StorageRootId, x.RoleId }).IsUnique();
            e.HasOne(x => x.StorageRoot).WithMany(x => x.Access).HasForeignKey(x => x.StorageRootId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Bookmark>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.SortOrder });
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.RelativePath).HasMaxLength(1024);
            e.Property(x => x.DisplayIcon).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.StorageRoot).WithMany().HasForeignKey(x => x.StorageRootId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ExplorerTab>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.SortOrder });
            e.Property(x => x.Title).HasMaxLength(128);
            e.Property(x => x.RelativePath).HasMaxLength(1024);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.StorageRoot).WithMany().HasForeignKey(x => x.StorageRootId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<UserSetting>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.SettingKey }).IsUnique();
            e.Property(x => x.SettingKey).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<JobTask>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Status });
            e.Property(x => x.JobType).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ResultPath).HasMaxLength(1024);
            e.Property(x => x.ErrorMessage).HasMaxLength(2048);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ActivityLog>(e =>
        {
            e.HasIndex(x => x.CreateDate);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.ActionType).HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetPath).HasMaxLength(1024);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
