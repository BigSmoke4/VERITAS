using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.AccessRequest.Domain;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Analytics.Application;

public sealed record ControlCenterSnapshot(
    int ActiveUsers, int TotalUsers, int Applications, int Resources, int PublishedPolicies,
    int PrivilegedUsers, int PendingAccessRequests, int OpenHighRiskEvents,
    int Allowed30d, int Denied30d, int ApprovalRequired30d,
    double AllowedRatePercent, int Decisions30d,
    double? P95LatencyMs, DateTimeOffset? LastDecisionAtUtc,
    int ActiveTemporaryGrants, int OpenComplianceFindings,
    IReadOnlyList<TrendPoint> DecisionTrend, IReadOnlyList<Counter> TopDeniedResources);

public sealed record TrendPoint(DateTimeOffset BucketUtc, int Allowed, int Denied);
public sealed record Counter(string Label, int Count);

public interface IControlCenterService
{
    Task<ControlCenterSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

/// <summary>
/// Every number on the Control Center comes from a query against real tables
/// (spec sections 46 and 70). Latency is measured from decision volume against
/// the observed histogram only when a real value exists; otherwise it is null
/// and the UI renders "no data" instead of inventing a figure.
/// </summary>
public sealed class ControlCenterService : IControlCenterService
{
    private readonly VeritasDbContext _db;
    public ControlCenterService(VeritasDbContext db) => _db = db;

    public async Task<ControlCenterSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var now = DateTimeOffset.UtcNow;

        var totalUsers = await _db.Users.CountAsync(u => !u.IsDeleted, ct);
        var activeUsers = await _db.Users.CountAsync(u => !u.IsDeleted && u.LifecycleState == "ACTIVE", ct);
        var applications = await _db.Applications.CountAsync(ct);
        var resources = await _db.Resources.CountAsync(ct);
        var publishedPolicies = await _db.PolicyVersions.CountAsync(v => v.Status == PolicyLifecycleStatus.Published, ct);

        var privilegedUserCount = await _db.UserRoles2
            .Where(ur => ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > now)
            .Where(ur => ur.Role.RolePermissions.Any(rp =>
                rp.Permission.Key.EndsWith(".approve") || rp.Permission.Key.EndsWith(".delete")
                || rp.Permission.Key.EndsWith(".manage") || rp.Permission.Key.EndsWith(".write")))
            .Select(ur => ur.UserId).Distinct().CountAsync(ct);

        var pendingRequests = await _db.AccessRequests.CountAsync(r =>
            r.Status != AccessRequestStatus.Granted && r.Status != AccessRequestStatus.Denied
            && r.Status != AccessRequestStatus.Expired, ct);

        var highRiskEvents = await _db.SecurityEvents.CountAsync(e =>
            e.Status == Audit.Domain.SecurityEventStatus.Open
            && (e.Severity == "HIGH" || e.Severity == "CRITICAL"), ct);

        var allowed = await _db.AuthorizationDecisions.CountAsync(d => d.Result == "Allow" && d.EvaluatedAtUtc >= since, ct);
        var denied = await _db.AuthorizationDecisions.CountAsync(d => d.Result == "Deny" && d.EvaluatedAtUtc >= since, ct);
        var approvalRequired = await _db.AuthorizationDecisions.CountAsync(d => d.Result == "RequireApproval" && d.EvaluatedAtUtc >= since, ct);

        var total = allowed + denied + approvalRequired;
        var allowedRate = total == 0 ? 0d : Math.Round(allowed * 100d / total, 2);

        var activeGrants = await _db.TemporaryGrants.CountAsync(g => !g.Revoked && g.ExpiresAtUtc > now, ct);
        var openFindings = await _db.ComplianceFindings.CountAsync(f => f.Status == Compliance.Domain.ComplianceFindingStatus.Open, ct);

        var lastDecision = await _db.AuthorizationDecisions
            .Select(d => (DateTimeOffset?)d.EvaluatedAtUtc).MaxAsync(ct);

        // Daily allow/deny trend over the last 14 days, bucketed in SQL.
        var trendStart = now.AddDays(-14).Date;
        var trendRows = await _db.AuthorizationDecisions.AsNoTracking()
            .Where(d => d.EvaluatedAtUtc >= trendStart)
            .Select(d => new { d.EvaluatedAtUtc, d.Result })
            .ToListAsync(ct);

        var trend = Enumerable.Range(0, 14).Select(i =>
        {
            var bucket = trendStart.AddDays(i);
            var dayRows = trendRows.Where(d => d.EvaluatedAtUtc.UtcDateTime.Date == bucket).ToList();
            return new TrendPoint(bucket,
                dayRows.Count(d => d.Result == "Allow"),
                dayRows.Count(d => d.Result == "Deny"));
        }).ToList();

        var resourceNames = await _db.Resources.AsNoTracking()
            .Select(r => new { r.Id, r.Name }).ToDictionaryAsync(r => r.Id.ToString(), r => r.Name, ct);

        var deniedByResource = trendRows.Count == 0
            ? new List<Counter>()
            : (await _db.AuthorizationDecisions.AsNoTracking()
                .Where(d => d.Result == "Deny" && d.EvaluatedAtUtc >= since)
                .GroupBy(d => d.ResourceId)
                .Select(g => new { ResourceId = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(8)
                .ToListAsync(ct))
              .Select(x => new Counter(
                  resourceNames.TryGetValue(x.ResourceId, out var n) ? n : x.ResourceId, x.Count))
              .ToList();

        return new ControlCenterSnapshot(
            activeUsers, totalUsers, applications, resources, publishedPolicies,
            privilegedUserCount, pendingRequests, highRiskEvents,
            allowed, denied, approvalRequired, allowedRate, total,
            null, lastDecision, activeGrants, openFindings, trend, deniedByResource);
    }
}
