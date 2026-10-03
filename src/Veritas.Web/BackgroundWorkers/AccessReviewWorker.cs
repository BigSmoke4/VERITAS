using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veritas.Web.Modules.AccessReview.Domain;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Marks overdue, incomplete access-review campaigns as overdue and raises a
/// security event so a stalled certification cannot sit silently (spec
/// section 25). Idempotent: it only writes when the campaign is still open and
/// has not already been flagged in the current window.
/// </summary>
public sealed class AccessReviewWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccessReviewWorker> _logger;
    private readonly TimeSpan _interval;

    public AccessReviewWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AccessReviewWorker> logger,
        IOptions<WorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = options.Value.AccessReviewPollInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AccessReviewWorker tick failed; will retry.");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var now = DateTimeOffset.UtcNow;

        var overdue = await db.Set<AccessReviewCampaign>()
            .IgnoreQueryFilters()
            .Where(c => c.DueAtUtc <= now && !c.IsDeleted)
            .ToListAsync(ct);

        if (overdue.Count == 0) return;

        var flagged = 0;
        foreach (var campaign in overdue)
        {
            var remaining = await db.Set<AccessReviewItem>()
                .IgnoreQueryFilters()
                .CountAsync(i => i.AccessReviewCampaignId == campaign.Id && i.Decision == null, ct);

            if (remaining == 0) continue;

            var dedupe = $"access-review-overdue:{campaign.Id}";
            var alreadyFlagged = await db.SecurityEvents
                .IgnoreQueryFilters()
                .AnyAsync(e => e.DedupeKey == dedupe, ct);
            if (alreadyFlagged) continue;

            db.SecurityEvents.Add(new SecurityEvent
            {
                OrganizationId = campaign.OrganizationId,
                Detector = "ACCESS_REVIEW_OVERDUE",
                Severity = "MEDIUM",
                Title = $"Access review overdue: {campaign.Name}",
                Detail = $"{remaining} of the campaign's items are still undecided and the due date has passed.",
                DedupeKey = dedupe
            });
            flagged++;
        }

        if (flagged > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Flagged {Count} overdue access review campaign(s).", flagged);
        }
    }
}
