using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Approval.Application;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.Notification.Application;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Modules.PrivilegedAccess.Application;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Modules.RiskManagement.Application;
using Veritas.Web.Modules.RoleManagement.Application;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;
using Microsoft.Extensions.Options;
using AccessRequestDomain = Veritas.Web.Modules.AccessRequest.Domain;

namespace Veritas.Web.Modules.AccessRequest.Application;

public static class ApproverRoles
{
    public const string Manager = "Manager";
    public const string ResourceOwner = "ResourceOwner";
    public const string SecurityAdministrator = "SecurityAdministrator";
}

public sealed record AccessRequestCommand(
    Guid RequestedByUserId, Guid ResourceId, string PermissionKey,
    int DurationMinutes, string BusinessJustification);

public sealed record AccessRequestSubmission(
    bool Succeeded, Guid? AccessRequestId, string? Error, IReadOnlyList<string> ApprovalChain, RiskAssessment? Risk);

public sealed record AccessRequestListItem(
    Guid Id, string Requester, string Resource, string PermissionKey, TimeSpan Duration,
    AccessRequestDomain.AccessRequestStatus Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc, int StepsDecided, int StepsTotal);

public sealed record AccessRequestDetail(
    Guid Id, string Requester, Guid RequesterUserId, string Resource, Guid ResourceId, string PermissionKey,
    string BusinessJustification, TimeSpan Duration, AccessRequestDomain.AccessRequestStatus Status,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? GrantedAtUtc, DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<AccessRequestDomain.ApprovalStep> Steps, RiskAssessment? Risk);

public interface IAccessRequestService
{
    Task<AccessRequestSubmission> SubmitAsync(AccessRequestCommand command, CancellationToken ct = default);
    Task<(IReadOnlyList<AccessRequestListItem> Items, int Total)> ListAsync(AccessRequestDomain.AccessRequestStatus? status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Every request that is still moving through its approval chain, regardless of which
    /// step it has reached. The approvals queue uses this so a request never disappears
    /// from view the moment the first approver acts on it.
    /// </summary>
    Task<(IReadOnlyList<AccessRequestListItem> Items, int Total)> ListPendingAsync(int page, int pageSize, CancellationToken ct = default);
    Task<AccessRequestDetail?> GetAsync(Guid accessRequestId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveApprovalChainAsync(Guid resourceId, CancellationToken ct = default);
}

/// <summary>
/// Access requests (spec sections 19-20). Submitting one:
///   1. refuses up front if it would breach Separation-of-Duties,
///   2. scores the request with the real risk engine so the approver sees why,
///   3. materialises the approval chain as persisted ApprovalStep rows,
/// and the final approval mints a real TemporaryGrant with an expiry the
/// expiration worker will enforce — nothing here is simulated.
/// </summary>
public sealed class AccessRequestService : IAccessRequestService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;
    private readonly IRiskEvaluationService _risk;
    private readonly ISeparationOfDutiesEvaluator _sod;
    private readonly IPrivilegedAccessService _privileged;
    private readonly INotificationService _notifications;
    private readonly SecurityOptions _security;
    private readonly Observability.VeritasMetrics _metrics;

    public AccessRequestService(
        VeritasDbContext db,
        ITenantContext tenant,
        IAuditService audit,
        IRiskEvaluationService risk,
        ISeparationOfDutiesEvaluator sod,
        IPrivilegedAccessService privileged,
        INotificationService notifications,
        IOptions<SecurityOptions> security,
        Observability.VeritasMetrics metrics)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
        _risk = risk;
        _sod = sod;
        _privileged = privileged;
        _notifications = notifications;
        _security = security.Value;
        _metrics = metrics;
    }

    public async Task<IReadOnlyList<string>> ResolveApprovalChainAsync(Guid resourceId, CancellationToken ct = default)
    {
        var resource = await _db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId, ct)
            ?? throw new InvalidOperationException("Resource not found in this tenant.");

        // Highly confidential production data always needs Security in the chain;
        // everything else stops at the resource owner.
        var needsSecurity = string.Equals(resource.Classification, "HIGHLY_CONFIDENTIAL", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(resource.Environment, "production", StringComparison.OrdinalIgnoreCase);

        return needsSecurity
            ? new[] { ApproverRoles.Manager, ApproverRoles.SecurityAdministrator }
            : new[] { ApproverRoles.Manager, ApproverRoles.ResourceOwner };
    }

    public async Task<AccessRequestSubmission> SubmitAsync(AccessRequestCommand command, CancellationToken ct = default)
    {
        var resource = await _db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == command.ResourceId, ct);
        if (resource is null)
            return new AccessRequestSubmission(false, null, "Resource not found in this tenant.", Array.Empty<string>(), null);

        var requested = TimeSpan.FromMinutes(command.DurationMinutes);
        if (requested > _security.MaxTemporaryGrantDuration)
            return new AccessRequestSubmission(false, null,
                $"Requested duration exceeds the platform maximum of {_security.MaxTemporaryGrantDuration}.", Array.Empty<string>(), null);

        // Separation of Duties is checked at request time, so the violation is
        // refused before any approver spends time on it (spec section 74).
        var violation = await _sod.CheckPermissionAsync(command.RequestedByUserId, command.PermissionKey, ct);
        if (violation is not null)
        {
            await _audit.RecordAsync(
                action: "ACCESS_REQUEST_DENIED_SOD",
                resourceId: command.ResourceId.ToString(),
                previousValue: null,
                newValue: $"{violation.PermissionKeyA} conflicts with {violation.PermissionKeyB}",
                decisionId: null,
                correlationId: command.RequestedByUserId.ToString(),
                ct: ct);

            _db.SecurityEvents.Add(new SecurityEvent
            {
                OrganizationId = _tenant.OrganizationId,
                Detector = "SEPARATION_OF_DUTIES_VIOLATION",
                Severity = "HIGH",
                Title = $"SoD violation attempted: {command.PermissionKey}",
                Detail = $"{violation.Description} ({violation.PermissionKeyA} vs {violation.PermissionKeyB})",
                SubjectUserId = command.RequestedByUserId,
                ResourceId = command.ResourceId.ToString(),
                DedupeKey = $"sod:{command.RequestedByUserId}:{command.PermissionKey}:{DateTimeOffset.UtcNow:yyyyMMddHH}"
            });
            await _db.SaveChangesAsync(ct);

            return new AccessRequestSubmission(false, null,
                $"Separation-of-Duties violation. Conflicting permissions: {violation.PermissionKeyA}, {violation.PermissionKeyB}",
                Array.Empty<string>(), null);
        }

        var chain = await ResolveApprovalChainAsync(command.ResourceId, ct);

        var risk = await _risk.EvaluateAsync(command.RequestedByUserId, new AuthorizationRequest
        {
            SubjectUserId = command.RequestedByUserId.ToString(),
            ResourceId = command.ResourceId.ToString(),
            Action = command.PermissionKey.Contains('.')
                ? command.PermissionKey[(command.PermissionKey.LastIndexOf('.') + 1)..]
                : command.PermissionKey,
            Environment = resource.Environment
        }, ct);
        _metrics.RecordRiskEvaluation();

        var request = new AccessRequestDomain.AccessRequest
        {
            OrganizationId = _tenant.OrganizationId,
            RequestedByUserId = command.RequestedByUserId,
            ResourceId = command.ResourceId,
            PermissionKey = command.PermissionKey,
            Duration = requested,
            BusinessJustification = command.BusinessJustification.Trim(),
            Status = AccessRequestDomain.AccessRequestStatus.Requested
        };

        for (var i = 0; i < chain.Count; i++)
        {
            request.Steps.Add(new AccessRequestDomain.ApprovalStep
            {
                AccessRequestId = request.Id,
                Order = i + 1,
                ApproverRole = chain[i]
            });
        }

        _db.AccessRequests.Add(request);
        await _db.SaveChangesAsync(ct);
        _metrics.RecordAccessRequest();

        await _audit.RecordAsync("ACCESS_REQUESTED", command.ResourceId.ToString(), null,
            $"{command.PermissionKey} for {command.DurationMinutes}m (risk {risk.TotalScore})", null, request.Id.ToString(), ct);

        await _notifications.EnqueueAsync(
            command.RequestedByUserId, NotificationChannel.InApp, "ACCESS_REQUESTED",
            "Access request submitted",
            $"Your request for {command.PermissionKey} on {resource.Name} is awaiting {chain[0]} review.", ct);

        return new AccessRequestSubmission(true, request.Id, null, chain, risk);
    }

    /// <summary>Statuses that mean "still waiting for someone".</summary>
    private static readonly AccessRequestDomain.AccessRequestStatus[] PendingStatuses =
    {
        AccessRequestDomain.AccessRequestStatus.Requested,
        AccessRequestDomain.AccessRequestStatus.ManagerReview,
        AccessRequestDomain.AccessRequestStatus.ResourceOwnerReview,
        AccessRequestDomain.AccessRequestStatus.SecurityReview
    };

    public Task<(IReadOnlyList<AccessRequestListItem> Items, int Total)> ListAsync(
        AccessRequestDomain.AccessRequestStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.AccessRequests.AsNoTracking().AsQueryable();
        if (status is not null)
            query = query.Where(r => r.Status == status);

        return ProjectAsync(query, page, pageSize, ct);
    }

    public Task<(IReadOnlyList<AccessRequestListItem> Items, int Total)> ListPendingAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        var pending = PendingStatuses;
        var query = _db.AccessRequests.AsNoTracking().Where(r => pending.Contains(r.Status));
        return ProjectAsync(query, page, pageSize, ct);
    }

    private async Task<(IReadOnlyList<AccessRequestListItem> Items, int Total)> ProjectAsync(
        IQueryable<AccessRequestDomain.AccessRequest> query, int page, int pageSize, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);

        var users = await _db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var resources = await _db.Resources.AsNoTracking()
            .Select(r => new { r.Id, r.Name }).ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        var stepCounts = await _db.ApprovalSteps.AsNoTracking()
            .GroupBy(s => s.AccessRequestId)
            .Select(g => new { RequestId = g.Key, Total = g.Count(), Decided = g.Count(s => s.Decision != null) })
            .ToDictionaryAsync(x => x.RequestId, x => (x.Total, x.Decided), ct);

        var rows = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(r => new AccessRequestListItem(
            r.Id,
            users.TryGetValue(r.RequestedByUserId, out var un) ? un : r.RequestedByUserId.ToString(),
            resources.TryGetValue(r.ResourceId, out var rn) ? rn : r.ResourceId.ToString(),
            r.PermissionKey, r.Duration, r.Status, r.CreatedAtUtc, r.ExpiresAtUtc,
            stepCounts.TryGetValue(r.Id, out var sc) ? sc.Decided : 0,
            stepCounts.TryGetValue(r.Id, out var sc2) ? sc2.Total : 0)).ToList();

        return (items, total);
    }

    public async Task<AccessRequestDetail?> GetAsync(Guid accessRequestId, CancellationToken ct = default)
    {
        var request = await _db.AccessRequests.AsNoTracking()
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == accessRequestId, ct);
        if (request is null) return null;

        var requester = await _db.Users.AsNoTracking()
            .Where(u => u.Id == request.RequestedByUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        var resourceName = await _db.Resources.AsNoTracking()
            .Where(r => r.Id == request.ResourceId).Select(r => r.Name).FirstOrDefaultAsync(ct);

        var risk = await _db.RiskEvaluations.AsNoTracking()
            .Where(e => e.UserId == request.RequestedByUserId && e.EvaluatedAtUtc >= request.CreatedAtUtc.AddMinutes(-1))
            .OrderByDescending(e => e.EvaluatedAtUtc)
            .Select(e => new { e.TotalScore, e.Level, e.SignalsJson })
            .FirstOrDefaultAsync(ct);

        RiskAssessment? riskAssessment = null;
        if (risk is not null)
        {
            var signals = System.Text.Json.JsonSerializer.Deserialize<List<RiskSignalScore>>(risk.SignalsJson)
                          ?? new List<RiskSignalScore>();
            riskAssessment = new RiskAssessment { TotalScore = risk.TotalScore, Level = risk.Level, Breakdown = signals };
        }

        return new AccessRequestDetail(
            request.Id, requester ?? request.RequestedByUserId.ToString(), request.RequestedByUserId,
            resourceName ?? request.ResourceId.ToString(), request.ResourceId, request.PermissionKey,
            request.BusinessJustification, request.Duration, request.Status, request.CreatedAtUtc,
            request.GrantedAtUtc, request.ExpiresAtUtc,
            request.Steps.OrderBy(s => s.Order).ToList(), riskAssessment);
    }
}
