using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.AccessReview.Domain;

public class AccessReviewCampaign : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = default!;
    public Guid ReviewerUserId { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public List<AccessReviewItem> Items { get; set; } = new();
}

public enum AccessReviewDecisionType { Keep, Remove, Modify, Delegate }

public class AccessReviewItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid AccessReviewCampaignId { get; set; }
    public Guid SubjectUserId { get; set; }
    public string PermissionKey { get; set; } = default!;
    public AccessReviewDecisionType? Decision { get; set; }
    public string? DecisionNote { get; set; }
    public Guid? DelegatedToUserId { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
