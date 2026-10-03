using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.PermissionManagement.Application;
using Veritas.Web.Modules.RoleManagement.Application;

namespace Veritas.Web.Modules.RoleManagement.Presentation;

public sealed class RolesIndexViewModel
{
    public IReadOnlyList<RoleSummary> Roles { get; init; } = Array.Empty<RoleSummary>();
    public IReadOnlyList<SoDRuleSummary> SoDRules { get; init; } = Array.Empty<SoDRuleSummary>();
    public IReadOnlyList<PermissionSummary> Permissions { get; init; } = Array.Empty<PermissionSummary>();
}

[Authorize]
public sealed class RolesPageController : Controller
{
    private readonly IRoleService _roles;
    private readonly IPermissionService _permissions;

    public RolesPageController(IRoleService roles, IPermissionService permissions)
    {
        _roles = roles;
        _permissions = permissions;
    }

    public async Task<IActionResult> Index(CancellationToken ct) => View(new RolesIndexViewModel
    {
        Roles = await _roles.ListAsync(ct),
        SoDRules = await _roles.ListSoDRulesAsync(ct),
        Permissions = await _permissions.ListAsync(null, ct)
    });

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var role = await _roles.GetAsync(id, ct);
        if (role is null) return NotFound();

        ViewBag.AllPermissions = await _permissions.ListAsync(null, ct);
        return View(role);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string description, Guid[] permissionIds, CancellationToken ct)
    {
        var role = await _roles.CreateAsync(name, description, permissionIds ?? Array.Empty<Guid>(), ct);
        TempData["veritas.notice"] = $"Role '{role.Name}' created.";
        return RedirectToAction(nameof(Details), new { id = role.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPermissions(Guid id, Guid[] permissionIds, CancellationToken ct)
    {
        await _roles.SetPermissionsAsync(id, permissionIds ?? Array.Empty<Guid>(), ct);
        TempData["veritas.notice"] = "Role permissions updated and audited.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSoDRule(string permissionKeyA, string permissionKeyB, string description, CancellationToken ct)
    {
        await _roles.CreateSoDRuleAsync(permissionKeyA, permissionKeyB, description, ct);
        TempData["veritas.notice"] = "Separation-of-Duties rule created.";
        return RedirectToAction(nameof(Index));
    }
}
