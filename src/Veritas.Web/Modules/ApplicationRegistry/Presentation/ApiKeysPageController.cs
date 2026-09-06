using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.ApplicationRegistry.Application;
using Veritas.Web.Modules.ApplicationRegistry.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.ApplicationRegistry.Presentation;

public sealed class ApiKeysViewModel
{
    public List<ServiceAccount> ServiceAccounts { get; init; } = new();
    public Dictionary<Guid, List<ApiKey>> KeysByServiceAccount { get; init; } = new();

    /// <summary>Set exactly once, immediately after creation/rotation — never persisted, never shown again after this render.</summary>
    public CreatedApiKey? JustCreatedSecret { get; init; }
}

[Authorize]
public sealed class ApiKeysPageController : Controller
{
    private readonly VeritasDbContext _db;
    private readonly IApiKeyService _apiKeys;
    private readonly Veritas.Web.Shared.Domain.ITenantContext _tenant;

    public ApiKeysPageController(VeritasDbContext db, IApiKeyService apiKeys, Veritas.Web.Shared.Domain.ITenantContext tenant)
    {
        _db = db;
        _apiKeys = apiKeys;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var accounts = await _db.ServiceAccounts.ToListAsync(ct);
        var keysByAccount = new Dictionary<Guid, List<ApiKey>>();
        foreach (var acct in accounts)
            keysByAccount[acct.Id] = await _db.ApiKeys.Where(k => k.ServiceAccountId == acct.Id).ToListAsync(ct);

        return View(new ApiKeysViewModel { ServiceAccounts = accounts, KeysByServiceAccount = keysByAccount });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateServiceAccount(Guid applicationId, string name, string owner, CancellationToken ct)
    {
        _db.ServiceAccounts.Add(new ServiceAccount { OrganizationId = _tenant.OrganizationId, ApplicationId = applicationId, Name = name, Owner = owner });
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateKey(Guid serviceAccountId, string scopesCsv, int? ttlHours, CancellationToken ct)
    {
        var scopes = scopesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ttl = ttlHours.HasValue ? TimeSpan.FromHours(ttlHours.Value) : (TimeSpan?)null;
        var created = await _apiKeys.CreateAsync(serviceAccountId, scopes, ttl, ct);

        var accounts = await _db.ServiceAccounts.ToListAsync(ct);
        var keysByAccount = new Dictionary<Guid, List<ApiKey>>();
        foreach (var acct in accounts)
            keysByAccount[acct.Id] = await _db.ApiKeys.Where(k => k.ServiceAccountId == acct.Id).ToListAsync(ct);

        return View("~/Views/ApiKeysPage/Index.cshtml", new ApiKeysViewModel { ServiceAccounts = accounts, KeysByServiceAccount = keysByAccount, JustCreatedSecret = created });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid apiKeyId, CancellationToken ct)
    {
        await _apiKeys.RevokeAsync(apiKeyId, ct);
        return RedirectToAction(nameof(Index));
    }
}
