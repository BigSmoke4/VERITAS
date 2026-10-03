using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Pattern detection over the real audit + decision stream (spec section 31).
/// Four concrete detectors, each a query over stored rows:
///   1. repeated authorization denials by one actor in a rolling window,
///   2. rapid privilege escalation (several ROLE_ASSIGNED events, one actor),
///   3. unusual privileged access volume (many grants to one actor),
///   4. access-request bursts from one requester.
///
/// Findings are written to SecurityEvent with a stable DedupeKey, so a
/// repeating pattern updates rather than spamming the queue, and a unique
/// index on (OrganizationId, DedupeKey) makes a concurrent double-write
/// impossible.
/// </summary>
public sealed class SecurityDetectionWorker : BackgroundService
{
    private const int DenialThreshold = 5;
    private const int EscalationThreshold = 3;
    private const int PrivilegedBurstThreshold = 3;
    private const int RequestBurstThreshold = 4;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecurityDetectionWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _window = TimeSpan.FromMinutes(10);

    public SecurityDetectionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SecurityDetectionWorker> logger,
        IOptions<WorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = options.Value.SecurityDetectionPollInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SecurityDetectionWorker tick failed; will retry.");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var since = DateTimeOffset.UtcNow - _window;
        var bucket = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmm");
        var raised = 0;

        // 1. Repeated authorization denials.
        var deniers = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "AUTHORIZATION_DENIED" && a.TimestampUtc >= since && a.ActorUserId != null)
            .GroupBy(a => new { a.OrganizationId, a.ActorUserId })
            .Select(g => new { g.Key.OrganizationId, g.Key.ActorUserId, Count = g.Count() })
            .Where(g => g.Count >= DenialThreshold)
            .ToListAsync(ct);

        foreach (var d in deniers)
            raised += await RaiseAsync(db, d.OrganizationId, d.ActorUserId, "REPEATED_DENIALS", "HIGH",
                $"{d.Count} authorization denials in {WindowMinutes} minutes",
                $"Actor was denied {d.Count} times within a {WindowMinutes}-minute window.",
                $"repeated-denials:{d.ActorUserId}:{bucket}", ct);

        // 2. Rapid privilege escalation.
        var escalators = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "ROLE_ASSIGNED" && a.TimestampUtc >= since)
            .GroupBy(a => new { a.OrganizationId, a.CorrelationId })
            .Select(g => new { g.Key.OrganizationId, g.Key.CorrelationId, Count = g.Count() })
            .Where(g => g.Count >= EscalationThreshold)
            .ToListAsync(ct);

        foreach (var e in escalators)
        {
            Guid.TryParse(e.CorrelationId, out var subjectId);
            raised += await RaiseAsync(db, e.OrganizationId, subjectId == Guid.Empty ? null : subjectId,
                "RAPID_PRIVILEGE_ESCALATION", "CRITICAL",
                $"{e.Count} role assignments in {WindowMinutes} minutes",
                "Multiple roles granted to the same subject inside one short window — possible privilege escalation.",
                $"rapid-escalation:{e.CorrelationId}:{bucket}", ct);
        }

        // 3. Unusual privileged access volume.
        var privilegedBurst = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "PRIVILEGED_ACCESS_GRANTED" && a.TimestampUtc >= since)
            .GroupBy(a => new { a.OrganizationId, a.CorrelationId })
            .Select(g => new { g.Key.OrganizationId, Count = g.Count() })
            .Where(g => g.Count >= PrivilegedBurstThreshold)
            .ToListAsync(ct);

        foreach (var p in privilegedBurst)
            raised += await RaiseAsync(db, p.OrganizationId, null, "UNUSUAL_PRIVILEGED_ACCESS", "HIGH",
                $"{p.Count} privileged grants in {WindowMinutes} minutes",
                "Privileged access is being granted far more often than the recent baseline.",
                $"privileged-burst:{p.OrganizationId}:{bucket}", ct);

        // 4. Access-request bursts.
        var requestBurst = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "ACCESS_REQUESTED" && a.TimestampUtc >= since)
            .GroupBy(a => new { a.OrganizationId, a.CorrelationId })
            .Select(g => new { g.Key.OrganizationId, Count = g.Count() })
            .Where(g => g.Count >= RequestBurstThreshold)
            .ToListAsync(ct);

        foreach (var r in requestBurst)
            raised += await RaiseAsync(db, r.OrganizationId, null, "ACCESS_REQUEST_BURST", "MEDIUM",
                $"{r.Count} access requests in {WindowMinutes} minutes",
                "A burst of access requests can indicate reconnaissance or a misconfigured client.",
                $"request-burst:{r.OrganizationId}:{bucket}", ct);

        if (raised > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Raised {Count} security event(s).", raised);
        }
    }

    private int WindowMinutes => (int)_window.TotalMinutes;

    /// <summary>Returns 1 when a new event was added, 0 when it was already raised.</summary>
    private static async Task<int> RaiseAsync(
        VeritasDbContext db, Guid organizationId, Guid? subjectUserId,
        string detector, string severity, string title, string detail, string dedupeKey, CancellationToken ct)
    {
        var exists = await db.SecurityEvents.IgnoreQueryFilters()
            .AnyAsync(e => e.DedupeKey == dedupeKey, ct);
        if (exists) return 0;

        db.SecurityEvents.Add(new SecurityEvent
        {
            OrganizationId = organizationId,
            Detector = detector,
            Severity = severity,
            Title = title,
            Detail = detail,
            SubjectUserId = subjectUserId,
            DedupeKey = dedupeKey
        });
        return 1;
    }
}
