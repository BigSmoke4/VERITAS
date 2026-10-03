using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.ApplicationRegistry.Application;
using Veritas.Web.Modules.ResourceManagement.Application;

namespace Veritas.Web.Modules.ResourceManagement.Presentation;

public sealed class ApplicationsIndexViewModel
{
    public IReadOnlyList<ApplicationSummary> Applications { get; init; } = Array.Empty<ApplicationSummary>();
}

[Authorize]
public sealed class ApplicationsPageController : Controller
{
    private readonly IResourceService _resources;
    private readonly IApiKeyService _apiKeys;

    public ApplicationsPageController(IResourceService resources, IApiKeyService apiKeys)
    {
        _resources = resources;
        _apiKeys = apiKeys;
    }

    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new ApplicationsIndexViewModel { Applications = await _resources.ListApplicationsAsync(ct) });

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var application = await _resources.GetApplicationAsync(id, ct);
        if (application is null) return NotFound();
        return View(application);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string owner, string environment, CancellationToken ct)
    {
        var created = await _resources.CreateApplicationAsync(name, owner, environment, ct);
        TempData["veritas.notice"] = $"Application '{created.Name}' registered.";
        return RedirectToAction(nameof(Details), new { id = created.Id });
    }
}

public sealed class ResourcesIndexViewModel
{
    public IReadOnlyList<ResourceSummary> Resources { get; init; } = Array.Empty<ResourceSummary>();
    public IReadOnlyList<ApplicationSummary> Applications { get; init; } = Array.Empty<ApplicationSummary>();
    public Guid? ApplicationId { get; init; }
    public string? Classification { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

[Authorize]
public sealed class ResourcesPageController : Controller
{
    private readonly IResourceService _resources;
    public ResourcesPageController(IResourceService resources) => _resources = resources;

    public async Task<IActionResult> Index(Guid? applicationId, string? classification, CancellationToken ct) =>
        View(new ResourcesIndexViewModel
        {
            Resources = await _resources.ListResourcesAsync(applicationId, classification, ct),
            Applications = await _resources.ListApplicationsAsync(ct),
            ApplicationId = applicationId,
            Classification = classification
        });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid applicationId, string name, string resourceType, string classification,
        string environment, string? ownerDepartment, string permissionKeyPrefix, CancellationToken ct)
    {
        var created = await _resources.CreateResourceAsync(
            applicationId, name, resourceType, classification, environment, ownerDepartment, permissionKeyPrefix, ct);
        TempData["veritas.notice"] = $"Resource '{created.Name}' created with permission prefix '{created.PermissionKeyPrefix}'.";
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>
/// Service-identity dashboard (spec section 55). Rotation returns the plaintext
/// secret exactly once and it is never persisted or logged.
/// </summary>
public sealed class ServiceIdentitiesIndexViewModel
{
    public IReadOnlyList<ApplicationSummary> Applications { get; init; } = Array.Empty<ApplicationSummary>();
    public IReadOnlyList<ServiceIdentityRow> Services { get; init; } = Array.Empty<ServiceIdentityRow>();
    public string? RevealedPlaintextKey { get; init; }
    public string? RevealedKeyPrefix { get; init; }
}

public sealed record ServiceIdentityRow(
    Guid Id, string Name, string Owner, string ApplicationName, string Status,
    DateTimeOffset? LastUsedAtUtc, DateTimeOffset? ExpiresAtUtc, IReadOnlyList<ApiKeyRow> ApiKeys);

public sealed record ApiKeyRow(Guid Id, string KeyPrefix, string Scopes, bool Revoked, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc);

[Authorize]
public sealed class ServiceIdentitiesPageController : Controller
{
    private readonly IResourceService _resources;
    private readonly IApiKeyService _apiKeys;
    private readonly Shared.Infrastructure.VeritasDbContext _db;

    public ServiceIdentitiesPageController(IResourceService resources, IApiKeyService apiKeys, Shared.Infrastructure.VeritasDbContext db)
    {
        _resources = resources;
        _apiKeys = apiKeys;
        _db = db;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var services = await _db.ServiceAccounts.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var keys = await _db.ApiKeys.AsNoTracking().ToListAsync(ct);
        var apps = await _resources.ListApplicationsAsync(ct);

        var rows = services.Select(s => new ServiceIdentityRow(
            s.Id, s.Name, s.Owner,
            apps.FirstOrDefault(a => a.Id == s.ApplicationId)?.Name ?? "(unknown)",
            s.Status, s.LastUsedAtUtc, s.ExpiresAtUtc,
            keys.Where(k => k.ServiceAccountId == s.Id)
                .OrderByDescending(k => k.CreatedAtUtc)
                .Select(k => new ApiKeyRow(k.Id, k.KeyPrefix, k.ScopesCsv, k.Revoked, k.ExpiresAtUtc, k.LastUsedAtUtc))
                .ToList())).ToList();

        return View(new ServiceIdentitiesIndexViewModel
        {
            Applications = apps,
            Services = rows,
            RevealedPlaintextKey = TempData["veritas.revealedKey"] as string,
            RevealedKeyPrefix = TempData["veritas.revealedPrefix"] as string
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueKey(Guid serviceAccountId, string scopes, int? expiresInDays, CancellationToken ct)
    {
        var scopeList = (scopes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ttl = expiresInDays is > 0 ? TimeSpan.FromDays(expiresInDays.Value) : (TimeSpan?)null;

        var created = await _apiKeys.CreateAsync(serviceAccountId, scopeList, ttl, ct);

        // One-time reveal through TempData; the plaintext never touches storage.
        TempData["veritas.revealedKey"] = created.PlaintextSecret;
        TempData["veritas.revealedPrefix"] = created.KeyPrefix;
        TempData["veritas.notice"] = "API key created. Copy the secret now — it is shown only once and only its hash is stored.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateKey(Guid apiKeyId, CancellationToken ct)
    {
        var created = await _apiKeys.RotateAsync(apiKeyId, ct);
        TempData["veritas.revealedKey"] = created.PlaintextSecret;
        TempData["veritas.revealedPrefix"] = created.KeyPrefix;
        TempData["veritas.notice"] = "Key rotated: the previous key is revoked and the new secret is shown once.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeKey(Guid apiKeyId, CancellationToken ct)
    {
        await _apiKeys.RevokeAsync(apiKeyId, ct);
        TempData["veritas.notice"] = "API key revoked.";
        return RedirectToAction(nameof(Index));
    }
}
