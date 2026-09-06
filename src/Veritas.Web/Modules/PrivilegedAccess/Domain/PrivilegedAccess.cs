using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.PrivilegedAccess.Domain;

/// <summary>
/// Persistent JIT grant. Expiration is enforced by TemporaryAccessExpirationWorker
/// reading this table — NOT by an in-memory Timer/Task.Delay, so a process
/// restart, multi-instance deployment, or crash cannot leave a grant active
/// past its ExpiresAtUtc (spec section 21).
/// </summary>
public class TemporaryGrant : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid ResourceId { get; set; }
    public string PermissionKey { get; set; } = default!;
    public string Reason { get; set; } = default!;
    public DateTimeOffset StartAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedReason { get; set; }

    public bool IsActive(DateTimeOffset nowUtc) => !Revoked && nowUtc < ExpiresAtUtc;
}

public enum PrivilegedSessionStatus
{
    PendingApproval,
    Active,
    Ended,
    Revoked
}

public class PrivilegedAccessRequest : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid ResourceId { get; set; }
    public string RiskLevel { get; set; } = "HIGH";
    public TimeSpan RequestedDuration { get; set; }
    public bool ApprovalRequired { get; set; } = true;
    public PrivilegedSessionStatus Status { get; set; } = PrivilegedSessionStatus.PendingApproval;
}

public class PrivilegedSession : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid PrivilegedAccessRequestId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string Status { get; set; } = "ACTIVE";
}
