using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Infrastructure.Idempotency;
using Veritas.Web.Modules.Approval.Application;

namespace Veritas.Web.Modules.Approval.Presentation;

public sealed record SubmitApprovalDecisionDto(
    Guid ApproverUserId, string ApproverRole, bool Approved, string? Comment, ApprovalStrategy Strategy);

public sealed record ApprovalDecisionResponse(
    Guid AccessRequestId, string Status, bool Succeeded, string? Error, Guid? TemporaryGrantId);

[ApiController]
[Route("api/v1/access-requests/{accessRequestId:guid}/approvals")]
[Authorize]
[EnableRateLimiting("access-request-api")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly IApprovalWorkflowService _workflow;
    private readonly IIdempotencyService _idempotency;

    public ApprovalsController(IApprovalWorkflowService workflow, IIdempotencyService idempotency)
    {
        _workflow = workflow;
        _idempotency = idempotency;
    }

    /// <summary>
    /// Submits one approver's decision on one step of a real approval chain.
    /// Denials, escalations and concurrent-approval races are handled by
    /// ApprovalWorkflowService; this controller only maps HTTP concerns.
    /// An Idempotency-Key makes a retried approval a no-op rather than a
    /// second decision on the same step.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApprovalDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SubmitDecision(
        Guid accessRequestId,
        [FromBody] SubmitApprovalDecisionDto dto,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (dto.ApproverUserId == Guid.Empty || string.IsNullOrWhiteSpace(dto.ApproverRole))
            return BadRequest(new { error = "approverUserId and approverRole are required." });

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var cached = await _idempotency.TryGetCachedResponseAsync(idempotencyKey, ct);
            if (cached is not null)
                return Ok(System.Text.Json.JsonSerializer.Deserialize<ApprovalDecisionResponse>(cached));
        }

        ApprovalOutcome outcome;
        try
        {
            outcome = await _workflow.SubmitDecisionAsync(
                accessRequestId, dto.ApproverUserId, dto.ApproverRole, dto.Approved, dto.Comment, dto.Strategy, ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // xmin concurrency token: two approvers raced on the same request.
            return Conflict(new { error = "This access request was modified concurrently by another approver. Please retry." });
        }

        var response = new ApprovalDecisionResponse(
            accessRequestId, outcome.Status.ToString(), outcome.Succeeded, outcome.Error, outcome.TemporaryGrantId);

        if (!outcome.Succeeded)
            return Conflict(response);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            await _idempotency.StoreResponseAsync(idempotencyKey,
                System.Text.Json.JsonSerializer.Serialize(response), TimeSpan.FromMinutes(10), ct);

        return Ok(response);
    }
}
