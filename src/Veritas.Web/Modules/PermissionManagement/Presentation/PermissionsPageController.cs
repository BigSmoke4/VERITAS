using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.PermissionManagement.Application;

namespace Veritas.Web.Modules.PermissionManagement.Presentation;

public sealed class PermissionsIndexViewModel
{
    public IReadOnlyList<PermissionSummary> Permissions { get; init; } = Array.Empty<PermissionSummary>();
    public IReadOnlyList<string> Prefixes { get; init; } = Array.Empty<string>();
    public string? Prefix { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

[Authorize]
public sealed class PermissionsPageController : Controller
{
    private readonly IPermissionService _permissions;
    public PermissionsPageController(IPermissionService permissions) => _permissions = permissions;

    public async Task<IActionResult> Index(string? prefix, CancellationToken ct) => View(new PermissionsIndexViewModel
    {
        Permissions = await _permissions.ListAsync(prefix, ct),
        Prefixes = await _permissions.ListPrefixesAsync(ct),
        Prefix = prefix
    });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string key, string description, CancellationToken ct)
    {
        try
        {
            var created = await _permissions.CreateAsync(key, description, ct);
            TempData["veritas.notice"] = $"Permission '{created.Key}' created.";
        }
        catch (InvalidOperationException ex)
        {
            return View(new PermissionsIndexViewModel
            {
                Permissions = await _permissions.ListAsync(null, ct),
                Prefixes = await _permissions.ListPrefixesAsync(ct),
                Errors = new[] { ex.Message }
            });
        }

        return RedirectToAction(nameof(Index));
    }
}
