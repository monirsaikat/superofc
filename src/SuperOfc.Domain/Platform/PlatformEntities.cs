using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Platform;

/// <summary>One audited mutation. Never updated or deleted.</summary>
public sealed class AuditEntry : CompanyEntity
{
    public string UserName { get; set; } = string.Empty;

    public DateTime AtUtc { get; set; }

    /// <summary>Verb such as "Employee.Created" or "Auth.LoginFailed".</summary>
    public string Action { get; set; } = string.Empty;

    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }
}

public enum SettingScope
{
    System = 0,
    Company = 1,
}

/// <summary>Key/value configuration stored as JSON.</summary>
public sealed class Setting : Entity
{
    public string Key { get; set; } = string.Empty;

    public string ValueJson { get; set; } = "null";

    public SettingScope Scope { get; set; }

    public Guid? CompanyId { get; set; }
}

/// <summary>A versioned policy section (attendance, payroll, ...) effective from a date.</summary>
public sealed class PolicyVersion : CompanyEntity
{
    public string Section { get; set; } = string.Empty;

    public DateOnly EffectiveFrom { get; set; }

    public string Json { get; set; } = "{}";

    public string? Note { get; set; }
}

public enum BackupType
{
    Auto = 0,
    Manual = 1,
    PreMigration = 2,
    PrePayroll = 3,
    PreRestore = 4,
}

public sealed class BackupRecord : Entity
{
    public string FilePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public BackupType Type { get; set; }

    public string AppVersion { get; set; } = string.Empty;

    public bool Verified { get; set; }

    public string? Note { get; set; }
}

/// <summary>Records which app version last migrated the database.</summary>
public sealed class SchemaInfo : Entity
{
    public string AppVersion { get; set; } = string.Empty;

    public string LastMigration { get; set; } = string.Empty;

    public DateTime MigratedAtUtc { get; set; }
}
