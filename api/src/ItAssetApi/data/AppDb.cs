using ItAssetApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace ItAssetApi.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.Username).IsUnique();
        b.Entity<Category>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Location>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Employee>().HasIndex(x => x.EmployeeNumber).IsUnique();

        b.Entity<Asset>(e =>
        {
            e.HasIndex(x => x.AssetTag).IsUnique();
            e.HasIndex(x => x.SerialNumber).IsUnique();      // NULLs are distinct in SQLite
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CategoryId);
        });

        b.Entity<Assignment>(e =>
        {
            // At most one active assignment per asset, enforced by the database
            e.HasIndex(x => x.AssetId).IsUnique().HasFilter("CheckedInAt IS NULL");
            e.HasIndex(x => new { x.CheckedOutBy, x.OutRequestKey }).IsUnique().HasFilter("OutRequestKey IS NOT NULL");
            e.HasIndex(x => new { x.CheckedInBy, x.InRequestKey }).IsUnique().HasFilter("InRequestKey IS NOT NULL");
            e.HasIndex(x => x.EmployeeId);
        });

        // At most one open maintenance record per asset
        b.Entity<MaintenanceRecord>().HasIndex(x => x.AssetId).IsUnique().HasFilter("ClosedAt IS NULL");

        b.Entity<AuditEvent>(e =>
        {
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.TargetId);
        });
    }

    /// <summary>Creates the schema and the audit-log append-only triggers.</summary>
    public void Initialize()
    {
        Database.EnsureCreated();
        Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON AuditEvents
            BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;
            """);
        Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON AuditEvents
            BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;
            """);
    }
}
