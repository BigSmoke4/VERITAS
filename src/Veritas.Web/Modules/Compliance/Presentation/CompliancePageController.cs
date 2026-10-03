using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.Compliance.Application;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Compliance.Presentation;

public sealed class ComplianceIndexViewModel
{
    public required IReadOnlyList<Compliance.Domain.CompliancePolicy> Controls { get; init; }
    public required IReadOnlyList<FindingSummary> Findings { get; init; }
    public required int TotalFindings { get; init; }
    public required IReadOnlyList<SecurityEvent> SecurityEvents { get; init; }
    public ScanResult? LastScan { get; init; }
    public string? Status { get; init; }
    public int Page { get; init; } = 1;
}

[Authorize]
public sealed class CompliancePageController : Controller
{
    private readonly IComplianceService _compliance;
    private readonly VeritasDbContext _db;

    public CompliancePageController(IComplianceService compliance, VeritasDbContext db)
    {
        _compliance = compliance;
        _db = db;
    }

    public async Task<IActionResult> Index(string? status, int page = 1, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        var (findings, total) = await _compliance.ListFindingsAsync(status, null, page, 25, ct);

        var events = await _db.SecurityEvents.AsNoTracking()
            .OrderByDescending(e => e.DetectedAtUtc)
            .Take(20)
            .ToListAsync(ct);

        return View(new ComplianceIndexViewModel
        {
            Controls = await _compliance.ListControlsAsync(ct),
            Findings = findings,
            TotalFindings = total,
            SecurityEvents = events,
            Status = status,
            Page = page
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Scan(CancellationToken ct)
    {
        var result = await _compliance.ScanAsync(ct);
        TempData["veritas.notice"] =
            $"Compliance scan complete: {result.Created} new finding(s), {result.Updated} refreshed, {result.AutoResolved} auto-resolved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Acknowledge(Guid findingId, string? status, int page, CancellationToken ct)
    {
        await _compliance.AcknowledgeAsync(findingId, ct);
        TempData["veritas.notice"] = "Finding acknowledged.";
        return RedirectToAction(nameof(Index), new { status, page });
    }
}
