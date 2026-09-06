using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Modules.Approval.Application;

namespace Veritas.Web.Modules.Approval.Presentation;

public sealed record SubmitApprovalDecisionDto(Guid ApproverUserId, string ApproverRole, bool Approved, string? Comment, ApprovalStrategy Strategy);

[ApiController]
[Route("api/v1/access-requests/{accessRequestId:guid}/approvals")]
[Authorize]
[EnableRateLimiting("access-request-api")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly IApprovalWorkflowService _workflow;
    public ApprovalsController(IApprovalWorkflowService workflow) => _workflow = workflow;

    /// <summary>
    /// Submits one approver's decision on one step of a real approval chain.
    /// Denies, escalations, and concurrent-approval races are all handled by
    /// the underlying ApprovalWorkflowService — this controller only maps
    /// HTTP concerns.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SubmitDecision(Guid accessRequestId, [FromBody] SubmitApprovalDecisionDto dto, CancellationToken ct)
    {
        try
        {
            var updated = await _workflow.SubmitDecisionAsync(
                accessRequestId, dto.ApproverUserId, dto.ApproverRole, dto.Approved, dto.Comment, dto.Strategy, ct);

            return Ok(new
            {
                accessRequestId = updated.Id,
                status = updated.Status.ToString(),
                grantedAtUtc = updated.GrantedAtUtc,
                expiresAtUtc = updated.ExpiresAtUtc
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "This access request was modified concurrently by another approver. Please retry." });
        }
    }
}
