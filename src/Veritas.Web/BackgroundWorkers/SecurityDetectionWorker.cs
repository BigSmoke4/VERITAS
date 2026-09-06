using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Detects a real, simple pattern from real audit data: N or more
/// AUTHORIZATION_DENIED events for the same actor within a rolling window.
/// This is intentionally one concrete, working detector rather than a fake
/// "AI threat engine" — it demonstrates the pattern (query audit data on an
/// interval, write a SecurityEvent-equivalent audit entry) that further
/// detectors would follow.
/// </summary>
public sealed class SecurityDetectionWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int FailureThreshold = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecurityDetectionWorker> _logger;

    public SecurityDetectionWorker(IServiceScopeFactory scopeFactory, ILogger<SecurityDetectionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DetectRepeatedDenialsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SecurityDetectionWorker tick failed; will retry.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DetectRepeatedDenialsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();

        var since = DateTimeOffset.UtcNow - Window;

        var offenders = await db.AuditLogs
            .IgnoreQueryFilters()
            .Where(a => a.Action == "AUTHORIZATION_DENIED" && a.TimestampUtc >= since && a.ActorUserId != null)
            .GroupBy(a => new { a.OrganizationId, a.ActorUserId })
            .Select(g => new { g.Key.OrganizationId, g.Key.ActorUserId, Count = g.Count() })
            .Where(g => g.Count >= FailureThreshold)
            .ToListAsync(ct);

        foreach (var offender in offenders)
        {
            // Avoid re-flagging the same actor every single tick: only write
            // a new finding if we haven't already flagged them in this window.
            var alreadyFlagged = await db.AuditLogs
                .IgnoreQueryFilters()
                .AnyAsync(a => a.Action == "SECURITY_EVENT_REPEATED_DENIALS"
                             && a.ActorUserId == offender.ActorUserId
                             && a.TimestampUtc >= since, ct);
            if (alreadyFlagged) continue;

            db.AuditLogs.Add(new AuditLog
            {
                OrganizationId = offender.OrganizationId,
                ActorUserId = offender.ActorUserId,
                Action = "SECURITY_EVENT_REPEATED_DENIALS",
                NewValue = $"{offender.Count} denials in {Window.TotalMinutes}min window",
                CorrelationId = Guid.NewGuid().ToString("N")
            });
        }

        if (offenders.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Flagged {Count} actor(s) for repeated authorization denials.", offenders.Count);
        }
    }
}
