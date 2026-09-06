using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Notification.Presentation;

public sealed class WebhookSubscriptionsViewModel
{
    public List<WebhookSubscription> Subscriptions { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Razor UI over the real WebhookSubscription CRUD — reads/writes the exact
/// same table as WebhookSubscriptionsController (the JSON API) and the same
/// rows WebhookNotificationTransport looks up at delivery time. No separate
/// in-memory list, no sample rows.
/// </summary>
[Authorize]
public sealed class WebhookSubscriptionsPageController : Controller
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;

    public WebhookSubscriptionsPageController(VeritasDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var subscriptions = await _db.WebhookSubscriptions.OrderByDescending(s => s.CreatedAtUtc).ToListAsync(ct);
        return View(new WebhookSubscriptionsViewModel { Subscriptions = subscriptions });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string eventType, string targetUrl, string? secretForSigning, CancellationToken ct)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            var subscriptions = await _db.WebhookSubscriptions.OrderByDescending(s => s.CreatedAtUtc).ToListAsync(ct);
            return View("~/Views/WebhookSubscriptionsPage/Index.cshtml", new WebhookSubscriptionsViewModel
            {
                Subscriptions = subscriptions,
                ErrorMessage = "Target URL must be a valid https:// address."
            });
        }

        _db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            OrganizationId = _tenant.OrganizationId,
            EventType = string.IsNullOrWhiteSpace(eventType) ? "*" : eventType,
            TargetUrl = targetUrl,
            SecretForSigning = string.IsNullOrWhiteSpace(secretForSigning) ? null : secretForSigning
        });
        await _db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleEnabled(Guid id, CancellationToken ct)
    {
        var sub = await _db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub is not null)
        {
            sub.Enabled = !sub.Enabled;
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var sub = await _db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub is not null)
        {
            _db.WebhookSubscriptions.Remove(sub);
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index));
    }
}
