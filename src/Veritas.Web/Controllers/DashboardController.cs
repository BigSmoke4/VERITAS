using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Controllers;

public sealed class DashboardViewModel
{
    public int Users { get; init; }
    public int Applications { get; init; }
    public int Resources { get; init; }
    public int PublishedPolicies { get; init; }
    public int PendingAccessRequests { get; init; }
    public int AllowedDecisions30d { get; init; }
    public int DeniedDecisions30d { get; init; }
}

[Authorize]
public sealed class DashboardController : Controller
{
    private readonly VeritasDbContext _db;

    public DashboardController(VeritasDbContext db) => _db = db;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);

        var vm = new DashboardViewModel
        {
            Users = await _db.Users.CountAsync(ct),
            Applications = await _db.Applications.CountAsync(ct),
            Resources = await _db.Resources.CountAsync(ct),
            PublishedPolicies = await _db.PolicyVersions
                .CountAsync(pv => pv.Status == Modules.PolicyManagement.Domain.PolicyLifecycleStatus.Published, ct),
            PendingAccessRequests = await _db.AccessRequests
                .CountAsync(r => r.Status != Modules.AccessRequest.Domain.AccessRequestStatus.Granted
                               && r.Status != Modules.AccessRequest.Domain.AccessRequestStatus.Denied
                               && r.Status != Modules.AccessRequest.Domain.AccessRequestStatus.Expired, ct),
            AllowedDecisions30d = await _db.AuthorizationDecisions
                .CountAsync(d => d.Result == AuthorizationDecisionResult.Allow.ToString() && d.EvaluatedAtUtc >= since, ct),
            DeniedDecisions30d = await _db.AuthorizationDecisions
                .CountAsync(d => d.Result == AuthorizationDecisionResult.Deny.ToString() && d.EvaluatedAtUtc >= since, ct),
        };

        return View(vm);
    }
}
