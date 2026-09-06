using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.AccessReview.Domain;
using Veritas.Web.Modules.RoleManagement.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.AccessReview.Application;

public sealed record ReviewProgress(int Total, int Completed, int Remaining, double PercentComplete);

public interface IAccessReviewService
{
    /// <summary>Builds review items from real current UserRole/RolePermission grants — not sample data.</summary>
    Task<AccessReviewCampaign> CreateCampaignFromCurrentGrantsAsync(string name, Guid reviewerUserId, DateTimeOffset dueAtUtc, CancellationToken ct = default);

    Task RecordDecisionAsync(Guid itemId, AccessReviewDecisionType decision, string? note, Guid? delegateToUserId, CancellationToken ct = default);

    Task<ReviewProgress> GetProgressAsync(Guid campaignId, CancellationToken ct = default);
}

/// <summary>
/// A REMOVE decision actually revokes the underlying UserRole grant — access
/// reviews here have teeth, they don't just record an opinion nobody acts on.
/// </summary>
public sealed class AccessReviewService : IAccessReviewService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public AccessReviewService(VeritasDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<AccessReviewCampaign> CreateCampaignFromCurrentGrantsAsync(string name, Guid reviewerUserId, DateTimeOffset dueAtUtc, CancellationToken ct = default)
    {
        var currentGrants = await _db.UserRoles2
            .Include(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .ToListAsync(ct);

        var campaign = new AccessReviewCampaign
        {
            OrganizationId = _tenant.OrganizationId,
            Name = name,
            ReviewerUserId = reviewerUserId,
            DueAtUtc = dueAtUtc
        };

        foreach (var grant in currentGrants)
        {
            foreach (var permission in grant.Role.RolePermissions.Select(rp => rp.Permission.Key).Distinct())
            {
                campaign.Items.Add(new AccessReviewItem
                {
                    OrganizationId = _tenant.OrganizationId,
                    AccessReviewCampaignId = campaign.Id,
                    SubjectUserId = grant.UserId,
                    PermissionKey = permission
                });
            }
        }

        _db.Set<AccessReviewCampaign>().Add(campaign);
        await _db.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task RecordDecisionAsync(Guid itemId, AccessReviewDecisionType decision, string? note, Guid? delegateToUserId, CancellationToken ct = default)
    {
        var item = await _db.Set<AccessReviewItem>().FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new InvalidOperationException("Access review item not found in this tenant.");

        item.Decision = decision;
        item.DecisionNote = note;
        item.DelegatedToUserId = delegateToUserId;
        item.DecidedAtUtc = DateTimeOffset.UtcNow;

        if (decision == AccessReviewDecisionType.Remove)
        {
            var role = await _db.RolePermissions
                .Where(rp => rp.Permission.Key == item.PermissionKey)
                .Select(rp => rp.RoleId)
                .Distinct()
                .ToListAsync(ct);

            var grant = await _db.UserRoles2
                .FirstOrDefaultAsync(ur => ur.UserId == item.SubjectUserId && role.Contains(ur.RoleId), ct);

            if (grant is not null)
            {
                _db.UserRoles2.Remove(grant);
            }
        }

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: $"ACCESS_REVIEW_{decision.ToString().ToUpperInvariant()}",
            resourceId: item.PermissionKey,
            previousValue: null,
            newValue: note,
            decisionId: null,
            correlationId: item.Id.ToString(),
            ct: ct);
    }

    public async Task<ReviewProgress> GetProgressAsync(Guid campaignId, CancellationToken ct = default)
    {
        var items = await _db.Set<AccessReviewItem>()
            .Where(i => i.AccessReviewCampaignId == campaignId)
            .ToListAsync(ct);

        var total = items.Count;
        var completed = items.Count(i => i.Decision is not null);
        var remaining = total - completed;
        var pct = total == 0 ? 0 : (double)completed / total * 100;

        return new ReviewProgress(total, completed, remaining, Math.Round(pct, 1));
    }
}
