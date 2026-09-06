using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Modules.AccessReview.Application;
using Veritas.Web.Modules.AccessReview.Domain;

namespace Veritas.Web.Modules.AccessReview.Presentation;

public sealed record RecordAccessReviewDecisionDto(
    AccessReviewDecisionType Decision,
    string? Note,
    Guid? DelegateToUserId);

/// <summary>API operations for recording access-review decisions.</summary>
[ApiController]
[Route("api/v1/access-review/items")]
[Authorize]
[EnableRateLimiting("admin-api")]
public sealed class AccessReviewApiController : ControllerBase
{
    private readonly IAccessReviewService _reviews;

    public AccessReviewApiController(IAccessReviewService reviews)
    {
        _reviews = reviews;
    }

    /// <summary>Records a Keep, Remove, Modify, or Delegate decision for an access-review item.</summary>
    /// <remarks>
    /// Example request — keep the current grant:
    ///
    ///     POST /api/v1/access-review/items/22222222-2222-2222-2222-222222222222/decision
    ///     {
    ///       "decision": "Keep",
    ///       "note": "Still required for the quarter-end payment process.",
    ///       "delegateToUserId": null
    ///     }
    ///
    /// Example request — remove the grant:
    ///
    ///     POST /api/v1/access-review/items/22222222-2222-2222-2222-222222222222/decision
    ///     {
    ///       "decision": "Remove",
    ///       "note": "No longer required.",
    ///       "delegateToUserId": null
    ///     }
    ///
    /// Example response: HTTP 204 No Content. A Remove decision also revokes the underlying role grant.
    /// </remarks>
    [HttpPost("{itemId:guid}/decision")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Decide(
        Guid itemId,
        [FromBody] RecordAccessReviewDecisionDto dto,
        CancellationToken ct)
    {
        try
        {
            await _reviews.RecordDecisionAsync(itemId, dto.Decision, dto.Note, dto.DelegateToUserId, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
