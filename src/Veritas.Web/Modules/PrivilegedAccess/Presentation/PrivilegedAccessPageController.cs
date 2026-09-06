using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.PrivilegedAccess.Application;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.PrivilegedAccess.Presentation;

public sealed class PrivilegedAccessViewModel
{
    public List<TemporaryGrant> ActiveGrants { get; init; } = new();
    public List<TemporaryGrant> RecentGrants { get; init; } = new();
    public List<(Guid Id, string Label)> Users { get; init; } = new();
    public List<(Guid Id, string Label)> Resources { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

[Authorize]
public sealed class PrivilegedAccessPageController : Controller
{
    private readonly VeritasDbContext _db;
    private readonly IPrivilegedAccessService _service;

    public PrivilegedAccessPageController(VeritasDbContext db, IPrivilegedAccessService service)
    {
        _db = db;
        _service = service;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var all = await _db.TemporaryGrants.OrderByDescending(g => g.CreatedAtUtc).Take(100).ToListAsync(ct);

        return View(new PrivilegedAccessViewModel
        {
            ActiveGrants = all.Where(g => g.IsActive(now)).ToList(),
            RecentGrants = all,
            Users = (await _db.Users.ToListAsync(ct)).Select(u => (u.Id, u.DisplayName ?? u.UserName ?? u.Id.ToString())).ToList(),
            Resources = (await _db.Resources.ToListAsync(ct)).Select(r => (r.Id, r.Name)).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Grant(Guid userId, Guid resourceId, string permissionKey, int durationMinutes, string reason, CancellationToken ct)
    {
        await _service.GrantTemporaryAccessAsync(userId, resourceId, permissionKey, TimeSpan.FromMinutes(durationMinutes), reason, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid grantId, string reason, CancellationToken ct)
    {
        await _service.RevokeAsync(grantId, reason, ct);
        return RedirectToAction(nameof(Index));
    }
}
