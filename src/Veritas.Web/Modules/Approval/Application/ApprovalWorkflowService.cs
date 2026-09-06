using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using AccessRequestDomain = Veritas.Web.Modules.AccessRequest.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Approval.Application;

public enum ApprovalStrategy
{
    Sequential,
    AllRequired,
    AnyOne
}

public interface IApprovalWorkflowService
{
    Task<AccessRequestDomain.AccessRequest> SubmitDecisionAsync(
        Guid accessRequestId,
        Guid approverUserId,
        string approverRole,
        bool approved,
        string? comment,
        ApprovalStrategy strategy = ApprovalStrategy.Sequential,
        CancellationToken ct = default);
}

/// <summary>
/// Advances an AccessRequest through its ApprovalStep chain. Sequential
/// strategy requires steps to be decided in Order; a denial at any step
/// immediately denies the whole request. AllRequired needs every step
/// approved (any order); AnyOne needs a single approval from any step's
/// role to grant. Concurrent double-approval races surface as
/// DbUpdateConcurrencyException via the AccessRequest's RowVersion, and the
/// caller must retry rather than silently double-process.
/// </summary>
public sealed class ApprovalWorkflowService : IApprovalWorkflowService
{
    private readonly VeritasDbContext _db;
    private readonly IAuditService _audit;

    public ApprovalWorkflowService(VeritasDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<AccessRequestDomain.AccessRequest> SubmitDecisionAsync(
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
            .FirstOrDefaultAsync(r => r.Id == accessRequestId, ct)
            ?? throw new InvalidOperationException("Access request not found in this tenant.");

        if (request.Status is AccessRequestDomain.AccessRequestStatus.Approved
            or AccessRequestDomain.AccessRequestStatus.Denied
            or AccessRequestDomain.AccessRequestStatus.Granted
            or AccessRequestDomain.AccessRequestStatus.Expired)
        {
            throw new InvalidOperationException($"Access request is already {request.Status} and cannot receive further decisions.");
        }

        var orderedSteps = request.Steps.OrderBy(s => s.Order).ToList();

        switch (strategy)
        {
            case ApprovalStrategy.Sequential:
            {
                var nextPending = orderedSteps.FirstOrDefault(s => s.Decision is null)
                    ?? throw new InvalidOperationException("No pending approval step.");
                if (!string.Equals(nextPending.ApproverRole, approverRole, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Step {nextPending.Order} requires role {nextPending.ApproverRole}, not {approverRole}.");

                nextPending.Decision = approved ? "APPROVED" : "DENIED";
                nextPending.DecidedByUserId = approverUserId;
                nextPending.DecidedAtUtc = DateTimeOffset.UtcNow;

                if (!approved)
                    request.Status = AccessRequestDomain.AccessRequestStatus.Denied;
                else if (orderedSteps.All(s => s.Decision == "APPROVED"))
                    Grant(request);
                break;
            }
            case ApprovalStrategy.AnyOne:
            {
                var step = orderedSteps.FirstOrDefault(s => string.Equals(s.ApproverRole, approverRole, StringComparison.OrdinalIgnoreCase) && s.Decision is null)
                    ?? throw new InvalidOperationException($"No pending step for role {approverRole}.");
                step.Decision = approved ? "APPROVED" : "DENIED";
                step.DecidedByUserId = approverUserId;
                step.DecidedAtUtc = DateTimeOffset.UtcNow;

                if (approved)
                    Grant(request);
                else if (orderedSteps.All(s => s.Decision == "DENIED"))
                    request.Status = AccessRequestDomain.AccessRequestStatus.Denied;
                break;
            }
            case ApprovalStrategy.AllRequired:
            {
                var step = orderedSteps.FirstOrDefault(s => string.Equals(s.ApproverRole, approverRole, StringComparison.OrdinalIgnoreCase) && s.Decision is null)
                    ?? throw new InvalidOperationException($"No pending step for role {approverRole}.");
                step.Decision = approved ? "APPROVED" : "DENIED";
                step.DecidedByUserId = approverUserId;
                step.DecidedAtUtc = DateTimeOffset.UtcNow;

                if (!approved)
                    request.Status = AccessRequestDomain.AccessRequestStatus.Denied;
                else if (orderedSteps.All(s => s.Decision == "APPROVED"))
                    Grant(request);
                break;
            }
        }

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: approved ? "ACCESS_APPROVED" : "ACCESS_DENIED",
            resourceId: request.ResourceId.ToString(),
            previousValue: null,
            newValue: request.Status.ToString(),
            decisionId: null,
            correlationId: request.Id.ToString(),
            ct: ct);

        return request;
    }

    private static void Grant(AccessRequestDomain.AccessRequest request)
    {
        request.GrantedAtUtc = DateTimeOffset.UtcNow;
        request.ExpiresAtUtc = request.GrantedAtUtc + request.Duration;
        request.Status = AccessRequestDomain.AccessRequestStatus.Granted;
    }
}
