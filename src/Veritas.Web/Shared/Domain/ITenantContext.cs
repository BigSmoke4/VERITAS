namespace Veritas.Web.Shared.Domain;

/// <summary>
/// Resolves the current tenant (Organization) for the executing request/worker scope.
/// Every tenant-scoped repository/query must go through this instead of trusting
/// client-supplied organization identifiers.
/// </summary>
public interface ITenantContext
{
    Guid OrganizationId { get; }
    bool IsResolved { get; }
}

/// <summary>
/// Base type for every entity that belongs to a single Organization (tenant).
/// EF Core global query filters use this to make cross-tenant reads structurally
/// impossible through the normal DbContext.
/// </summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; set; }
}

public abstract class AuditableEntity
{
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>EF Core optimistic-concurrency token (Postgres xmin).</summary>
    public uint RowVersion { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
}
