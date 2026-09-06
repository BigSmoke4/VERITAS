using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.AccessRequest.Domain;

public enum AccessRequestStatus
{
    Requested,
    ManagerReview,
    ResourceOwnerReview,
    SecurityReview,
    Approved,
    Denied,
    Granted,
    Expired
}

public class AccessRequest : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid ResourceId { get; set; }
    public string PermissionKey { get; set; } = default!;
    public TimeSpan Duration { get; set; }
    public string BusinessJustification { get; set; } = default!;
    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Requested;
    public DateTimeOffset? GrantedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public List<ApprovalStep> Steps { get; set; } = new();
}

public class ApprovalStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccessRequestId { get; set; }
    public int Order { get; set; }
    public string ApproverRole { get; set; } = default!;
    public Guid? DecidedByUserId { get; set; }
    public string? Decision { get; set; } // APPROVED / DENIED
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
