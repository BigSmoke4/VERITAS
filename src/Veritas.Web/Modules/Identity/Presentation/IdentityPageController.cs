using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.Identity.Application;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.RoleManagement.Application;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Identity.Presentation;

public sealed class IdentityIndexViewModel
{
    public IReadOnlyList<UserListItem> Users { get; init; } = Array.Empty<UserListItem>();
    public int Total { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Term { get; init; }
    public string? State { get; init; }
    public IReadOnlyList<DepartmentOption> Departments { get; init; } = Array.Empty<DepartmentOption>();
}

public sealed record DepartmentOption(Guid Id, string Name);

public sealed class IdentityDetailsViewModel
{
    public required UserDetail User { get; init; }
    public required IReadOnlyList<RoleSummary> Roles { get; init; }
    public required IReadOnlyList<string> AllowedTransitions { get; init; }
}

public sealed class IdentityCreateViewModel
{
    public IReadOnlyList<DepartmentOption> Departments { get; init; } = Array.Empty<DepartmentOption>();
    public IReadOnlyList<RoleSummary> Roles { get; init; } = Array.Empty<RoleSummary>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

[Authorize]
public sealed class IdentityPageController : Controller
{
    private readonly IIdentityUserService _users;
    private readonly IUserLifecycleService _lifecycle;
    private readonly IRoleService _roles;
    private readonly VeritasDbContext _db;

    public IdentityPageController(IIdentityUserService users, IUserLifecycleService lifecycle, IRoleService roles, VeritasDbContext db)
    {
        _users = users;
        _lifecycle = lifecycle;
        _roles = roles;
        _db = db;
    }

    public async Task<IActionResult> Index(string? term, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 10, 100);
        var (items, total) = await _users.SearchAsync(term, state, page, pageSize, ct);

        return View(new IdentityIndexViewModel
        {
            Users = items, Total = total, Page = page, PageSize = pageSize, Term = term, State = state,
            Departments = await DepartmentOptionsAsync(ct)
        });
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var user = await _users.GetAsync(id, ct);
        if (user is null) return NotFound();

        var allowed = UserLifecycleState.AllowedTransitions
            .TryGetValue(user.LifecycleState, out var next) ? next : Array.Empty<string>();

        return View(new IdentityDetailsViewModel
        {
            User = user,
            Roles = await _roles.ListAsync(ct),
            AllowedTransitions = allowed
        });
    }

    public async Task<IActionResult> Create(CancellationToken ct) => View(new IdentityCreateViewModel
    {
        Departments = await DepartmentOptionsAsync(ct),
        Roles = await _roles.ListAsync(ct)
    });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string displayName, string email, string jobTitle, Guid? departmentId, string clearance,
        string initialPassword, Guid[] roleIds, CancellationToken ct)
    {
        var result = await _users.CreateAsync(new CreateUserCommand(
            displayName, email, jobTitle, departmentId, clearance, initialPassword, roleIds ?? Array.Empty<Guid>()), ct);

        if (!result.Succeeded)
        {
            return View(new IdentityCreateViewModel
            {
                Departments = await DepartmentOptionsAsync(ct),
                Roles = await _roles.ListAsync(ct),
                Errors = result.Errors
            });
        }

        TempData["veritas.notice"] = result.Errors.Count == 0
            ? "User invited. They move to PENDING_VERIFICATION once their email is confirmed."
            : $"User invited, but some roles were refused: {string.Join("; ", result.Errors)}";

        return RedirectToAction(nameof(Details), new { id = result.UserId });
    }

    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var user = await _users.GetAsync(id, ct);
        if (user is null) return NotFound();

        ViewBag.User = user;
        ViewBag.Departments = await DepartmentOptionsAsync(ct);
        return View(user);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid id, string displayName, string jobTitle, Guid? departmentId, string clearance,
        string location, string country, CancellationToken ct)
    {
        var result = await _users.UpdateAsync(id, displayName, jobTitle, departmentId, clearance, location, country, ct);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
            return await Edit(id, ct);
        }

        TempData["veritas.notice"] = "Profile updated and audited.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transition(Guid id, string targetState, string? reason, CancellationToken ct)
    {
        var result = await _lifecycle.TransitionAsync(id, targetState, reason, ct);
        TempData["veritas.notice"] = result.Succeeded
            ? $"Lifecycle transition {result.From} -> {result.To} recorded."
            : $"Transition refused: {result.Error}";

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignRole(Guid id, Guid roleId, int? expiresInDays, CancellationToken ct)
    {
        var expiry = expiresInDays is > 0 ? DateTimeOffset.UtcNow.AddDays(expiresInDays.Value) : (DateTimeOffset?)null;
        var outcome = await _users.AssignRoleAsync(id, roleId, expiry, ct);

        TempData["veritas.notice"] = outcome.Succeeded
            ? "Role assigned and audited."
            : $"Role assignment denied — {outcome.Error}";

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeRole(Guid id, Guid roleId, CancellationToken ct)
    {
        await _users.RevokeRoleAsync(id, roleId, ct);
        TempData["veritas.notice"] = "Role revoked and audited.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IReadOnlyList<DepartmentOption>> DepartmentOptionsAsync(CancellationToken ct) =>
        await _db.Departments.AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentOption(d.Id, d.Name))
            .ToListAsync(ct);
}
