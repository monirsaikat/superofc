namespace SuperOfc.Domain.Common;

/// <summary>Base for every persisted entity. Audit fields are filled by the persistence layer.</summary>
public abstract class Entity
{
    public Guid Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? ModifiedAtUtc { get; set; }

    public string? ModifiedBy { get; set; }

    /// <summary>Optimistic concurrency token, incremented on every save.</summary>
    public long RowVersion { get; set; }
}

/// <summary>Entity that belongs to one company (multi-tenant seam).</summary>
public abstract class CompanyEntity : Entity
{
    public Guid CompanyId { get; set; }
}

/// <summary>Master data that is hidden instead of deleted.</summary>
public interface ISoftDeletable
{
    DateTime? DeletedAtUtc { get; set; }
}
