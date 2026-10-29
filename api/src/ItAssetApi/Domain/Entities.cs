namespace ItAssetApi.Domain;

public enum AssetStatus { Available, InUse, Maintenance, Retired }

public static class Roles
{
    public const string Admin = "Admin";
    public const string ITStaff = "ITStaff";
    public const string Auditor = "Auditor";
    public static readonly string[] All = [Admin, ITStaff, Auditor];
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Auditor;
    public Guid? EmployeeId { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>Rotated on password change/reset/deactivation so existing sessions stop validating.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public interface IMaster
{
    Guid Id { get; set; }
    string Name { get; set; }
    string? Description { get; set; }
    bool Active { get; set; }
}

public class Category : IMaster
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Active { get; set; } = true;
}

public class Location : IMaster
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Active { get; set; } = true;
}

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EmployeeNumber { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Department { get; set; }
    public string? Email { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AssetTag { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public string? SerialNumber { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Available;
    public Guid? LocationId { get; set; }
    public Location? Location { get; set; }
    public string? Notes { get; set; }
    /// <summary>Optimistic concurrency token; incremented on every change.</summary>
    public int Version { get; set; }
    public string? RetireReason { get; set; }
    public DateTime? RetiredAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid UpdatedBy { get; set; }
}

public class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateTime CheckedOutAt { get; set; } = DateTime.UtcNow;
    public DateTime? DueAt { get; set; }
    public Guid CheckedOutBy { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public Guid? CheckedInBy { get; set; }
    public string? ConditionOut { get; set; }
    public string? ConditionIn { get; set; }
    public string? Notes { get; set; }
    public string? OutRequestKey { get; set; }
    public string? InRequestKey { get; set; }
}

public class MaintenanceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public Guid ReportedBy { get; set; }
    public string? Provider { get; set; }
    public string Issue { get; set; } = "";
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public decimal? Cost { get; set; }
    public string? Resolution { get; set; }
}

public class AuditEvent
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string? TargetId { get; set; }
    public string Outcome { get; set; } = "success";
    public string RequestId { get; set; } = "";
    public string? SourceIp { get; set; }
    /// <summary>JSON of allow-listed fields only. Never credentials, tokens or free-text notes.</summary>
    public string? Metadata { get; set; }
}
