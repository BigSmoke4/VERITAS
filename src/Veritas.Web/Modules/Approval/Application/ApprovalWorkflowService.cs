using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.Notification.Application;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Modules.PrivilegedAccess.Application;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using AccessRequestDomain = Veritas.Web.Modules.AccessRequest.Domain;
using Veritas.Web.Observability;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Approval.Application;

public enum ApprovalStrategy
{
    /// <summary>Steps must be decided in Order; the next undecided step's role must match the approver.</summary>
    Sequential,

    /// <summary>Every step must approve, in any order. Any denial fails the request.</summary>
    AllRequired,

    /// <summary>A single approval from any listed role grants the request.</summary>
    AnyOne
}

public sealed record ApprovalOutcome(
    bool Succeeded, AccessRequestDomain.AccessRequestStatus Status, string? Error, Guid? TemporaryGrantId);

public interface IApprovalWorkflowService
{
    Task<ApprovalOutcome> SubmitDecisionAsync(
        Guid accessRequestId,
        Guid approverUserId,
        string approverRole,
        bool approved,
        string? comment,
        ApprovalStrategy strategy = ApprovalStrategy.Sequential,
        CancellationToken ct = default);
}

/// <summary>
/// Advances an AccessRequest through its ApprovalStep chain.
///
/// Concurrency: AccessRequest carries a Postgres xmin RowVersion, so two
/// approvers clicking "approve" at the same instant cannot both succeed — the
/// loser gets DbUpdateConcurrencyException from SaveChanges and must retry,
/// rather than the request being granted twice (spec section 34).
///
/// On final approval this mints a real TemporaryGrant whose expiry the
/// TemporaryAccessExpirationWorker enforces, so "approved" is not a label —
/// it is a working, expiring entitlement.
/// </summary>
public sealed class ApprovalWorkflowService : IApprovalWorkflowService
{
    private readonly VeritasDbContext _db;
    private readonly IAuditService _audit;
    private readonly IPrivilegedAccessService _privileged;
    private readonly INotificationService _notifications;
    private readonly VeritasMetrics _metrics;

    public ApprovalWorkflowService(
        VeritasDbContext db,
        IAuditService audit,
        IPrivilegedAccessService privileged,
        INotificationService notifications,
        VeritasMetrics metrics)
    {
        _db = db;
        _audit = audit;
        _privileged = privileged;
        _notifications = notifications;
        _metrics = metrics;
    }

    public async Task<ApprovalOutcome> SubmitDecisionAsync(
        Guid accessRequestId,
        Guid approverUserId,
        string approverRole,
        bool approved,
        string? comment,
        ApprovalStrategy strategy = ApprovalStrategy.Sequential,
        CancellationToken ct = default)
    {
        var request = await _db.AccessRequests
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == accessRequestId, ct);

        if (request is null)
            return new ApprovalOutcome(false, AccessRequestDomain.AccessRequestStatus.Requested,
                "Access request not found in this tenant.", null);

        if (request.Status is AccessRequestDomain.AccessRequestStatus.Approved
            or AccessRequestDomain.AccessRequestStatus.Denied
            or AccessRequestDomain.AccessRequestStatus.Granted
            or AccessRequestDomain.AccessRequestStatus.Expired)
        {
            return new ApprovalOutcome(false, request.Status,
                $"Access request is already {request.Status} and cannot receive further decisions.", null);
        }

        var orderedSteps = request.Steps.OrderBy(s => s.Order).ToList();
        AccessRequestDomain.ApprovalStep step;

        switch (strategy)
        {
            case ApprovalStrategy.Sequential:
                var nextPending = orderedSteps.FirstOrDefault(s => s.Decision is null);
                if (nextPending is null)
                    return new ApprovalOutcome(false, request.Status, "No pending approval step.", null);
                if (!string.Equals(nextPending.ApproverRole, approverRole, StringComparison.OrdinalIgnoreCase))
                    return new ApprovalOutcome(false, request.Status,
                        $"Step {nextPending.Order} requires role {nextPending.ApproverRole}, not {approverRole}.", null);
                step = nextPending;
                break;

            case ApprovalStrategy.AnyOne:
            case ApprovalStrategy.AllRequired:
                var candidate = orderedSteps.FirstOrDefault(s =>
                    string.Equals(s.ApproverRole, approverRole, StringComparison.OrdinalIgnoreCase) && s.Decision is null);
                if (candidate is null)
                    return new ApprovalOutcome(false, request.Status, $"No pending step for role {approverRole}.", null);
                step = candidate;
                break;

            default:
                return new ApprovalOutcome(false, request.Status, "Unknown approval strategy.", null);
        }

        step.Decision = approved ? "APPROVED" : "DENIED";
        step.DecidedByUserId = approverUserId;
        step.DecidedAtUtc = DateTimeOffset.UtcNow;
        step.Comment = comment;

        if (!approved)
        {
            request.Status = AccessRequestDomain.AccessRequestStatus.Denied;
        }
        else if (strategy == ApprovalStrategy.AnyOne
                 || orderedSteps.All(s => s.Decision == "APPROVED"))
        {
            request.Status = AccessRequestDomain.AccessRequestStatus.Granted;
            request.GrantedAtUtc = DateTimeOffset.UtcNow;
            request.ExpiresAtUtc = request.GrantedAtUtc + request.Duration;
        }
        else
        {
            request.Status = AdvanceReviewState(request.Status);
        }

        await _db.SaveChangesAsync(ct);
        _metrics.RecordAccessApproval();

        Guid? grantId = null;
        if (request.Status == AccessRequestDomain.AccessRequestStatus.Granted)
        {
            var grant = await _privileged.GrantTemporaryAccessAsync(
                request.RequestedByUserId,
                request.ResourceId,
                request.PermissionKey,
                request.Duration,
                $"Access request {request.Id:N}: {request.BusinessJustification}",
                ct);
            grantId = grant.Id;
        }

        await _audit.RecordAsync(
            action: approved ? "ACCESS_APPROVED" : "ACCESS_DENIED",
            resourceId: request.ResourceId.ToString(),
            previousValue: null,
            newValue: $"{request.Status} (step {step.Order} by {approverRole})",
            decisionId: null,
            correlationId: request.Id.ToString(),
            ct: ct);

        await _notifications.EnqueueAsync(
            request.RequestedByUserId,
            NotificationChannel.InApp,
            approved ? "ACCESS_APPROVED" : "ACCESS_DENIED",
            approved ? "Access approved" : "Access denied",
            approved
                ? (request.Status == AccessRequestDomain.AccessRequestStatus.Granted
                    ? $"Your access to {request.PermissionKey} is granted until {request.ExpiresAtUtc:u} UTC."
                    : $"Step {step.Order} ({approverRole}) approved. Awaiting the next approver.")
                : $"Step {step.Order} ({approverRole}) denied your request." + (comment is null ? "" : $" Comment: {comment}"),
            ct);

        return new ApprovalOutcome(true, request.Status, null, grantId);
    }

    private static AccessRequestDomain.AccessRequestStatus AdvanceReviewState(
        AccessRequestDomain.AccessRequestStatus current) => current switch
        {
            AccessRequestDomain.AccessRequestStatus.Requested => AccessRequestDomain.AccessRequestStatus.ManagerReview,
            AccessRequestDomain.AccessRequestStatus.ManagerReview => AccessRequestDomain.AccessRequestStatus.ResourceOwnerReview,
            AccessRequestDomain.AccessRequestStatus.ResourceOwnerReview => AccessRequestDomain.AccessRequestStatus.SecurityReview,
            AccessRequestDomain.AccessRequestStatus.SecurityReview => AccessRequestDomain.AccessRequestStatus.Approved,
            _ => current
        };
}
