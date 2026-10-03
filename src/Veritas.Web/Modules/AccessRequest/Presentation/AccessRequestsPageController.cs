using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.AccessRequest.Application;
using Veritas.Web.Modules.Identity.Application;
using Veritas.Web.Modules.ResourceManagement.Application;
using AccessRequestDomain = Veritas.Web.Modules.AccessRequest.Domain;

namespace Veritas.Web.Modules.AccessRequest.Presentation;

public sealed class AccessRequestsIndexViewModel
{
    public IReadOnlyList<AccessRequestListItem> Items { get; init; } = Array.Empty<AccessRequestListItem>();
    public int Total { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public AccessRequestDomain.AccessRequestStatus? Status { get; init; }
}

public sealed class AccessRequestCreateViewModel
{
    public IReadOnlyList<ResourceSummary> Resources { get; init; } = Array.Empty<ResourceSummary>();
    public IReadOnlyList<UserListItem> Users { get; init; } = Array.Empty<UserListItem>();
    public string? Error { get; init; }
}

[Authorize]
public sealed class AccessRequestsPageController : Controller
{
    private readonly IAccessRequestService _requests;
    private readonly IResourceService _resources;
    private readonly IIdentityUserService _users;

    public AccessRequestsPageController(IAccessRequestService requests, IResourceService resources, IIdentityUserService users)
    {
        _requests = requests;
        _resources = resources;
        _users = users;
    }

    public async Task<IActionResult> Index(string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 10, 100);
        AccessRequestDomain.AccessRequestStatus? parsed = null;
        if (Enum.TryParse<AccessRequestDomain.AccessRequestStatus>(status, ignoreCase: true, out var s))
            parsed = s;

        var (items, total) = await _requests.ListAsync(parsed, page, pageSize, ct);
        return View(new AccessRequestsIndexViewModel
        {
            Items = items, Total = total, Page = page, PageSize = pageSize, Status = parsed
        });
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var detail = await _requests.GetAsync(id, ct);
        if (detail is null) return NotFound();
        return View(detail);
    }

    public async Task<IActionResult> Create(CancellationToken ct) => View(new AccessRequestCreateViewModel
    {
        Resources = await _resources.ListResourcesAsync(null, null, ct),
        Users = (await _users.SearchAsync(null, null, 1, 100, ct)).Items
    });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid requestedByUserId, Guid resourceId, string permissionKey, int durationMinutes,
        string businessJustification, CancellationToken ct)
    {
        var submission = await _requests.SubmitAsync(new AccessRequestCommand(
            requestedByUserId, resourceId, permissionKey, durationMinutes, businessJustification), ct);

        if (!submission.Succeeded)
        {
            return View(new AccessRequestCreateViewModel
            {
                Resources = await _resources.ListResourcesAsync(null, null, ct),
                Users = (await _users.SearchAsync(null, null, 1, 100, ct)).Items,
                Error = submission.Error
            });
        }

        TempData["veritas.notice"] = $"Access request created. Approval chain: {string.Join(" -> ", submission.ApprovalChain)}.";
        return RedirectToAction(nameof(Details), new { id = submission.AccessRequestId });
    }
}
