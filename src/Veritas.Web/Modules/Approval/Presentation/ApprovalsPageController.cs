using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.AccessRequest.Application;
using Veritas.Web.Modules.Approval.Application;
using AccessRequestDomain = Veritas.Web.Modules.AccessRequest.Domain;

namespace Veritas.Web.Modules.Approval.Presentation;

public sealed class ApprovalsQueueViewModel
{
    public IReadOnlyList<AccessRequestListItem> Items { get; init; } = Array.Empty<AccessRequestListItem>();
    public int Total { get; init; }
}

[Authorize]
public sealed class ApprovalsPageController : Controller
{
    private readonly IAccessRequestService _requests;
    private readonly IApprovalWorkflowService _workflow;

    public ApprovalsPageController(IAccessRequestService requests, IApprovalWorkflowService workflow)
    {
        _requests = requests;
        _workflow = workflow;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // The queue covers every in-flight status, not just Requested: once the first
        // approver acts the request moves to ManagerReview/ResourceOwnerReview/SecurityReview
        // and must still be visible to whoever is next.
        var (items, total) = await _requests.ListPendingAsync(1, 100, ct);
        return View(new ApprovalsQueueViewModel { Items = items, Total = total });
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var detail = await _requests.GetAsync(id, ct);
        if (detail is null) return NotFound();
        return View(detail);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(
        Guid id, Guid approverUserId, string approverRole, bool approved, string? comment, CancellationToken ct)
    {
        var outcome = await _workflow.SubmitDecisionAsync(id, approverUserId, approverRole, approved, comment, ApprovalStrategy.Sequential, ct);

        TempData["veritas.notice"] = outcome.Succeeded
            ? (outcome.Status == AccessRequestDomain.AccessRequestStatus.Granted
                ? "Approved. A temporary grant was issued and its expiry is now enforced by the expiration worker."
                : $"Decision recorded. Request is now {outcome.Status}.")
            : $"Decision refused: {outcome.Error}";

        return RedirectToAction(nameof(Details), new { id });
    }
}
