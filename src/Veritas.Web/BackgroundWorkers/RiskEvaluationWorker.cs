using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Rolls each user's most recent risk evaluations up onto the user record, so
/// "high risk user" is a queryable fact rather than something recomputed per
/// request, and raises a security event when a user crosses into CRITICAL.
/// Derived strictly from stored RiskEvaluation rows — never a random or
/// hand-set risk level (spec sections 17 and 70).
/// </summary>
public sealed class RiskEvaluationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RiskEvaluationWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public RiskEvaluationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<RiskEvaluationWorker> logger,
        IOptions<WorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = options.Value.RiskEvaluationPollInterval;
        _batchSize = options.Value.BatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "RiskEvaluationWorker tick failed; will retry.");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var since = DateTimeOffset.UtcNow.AddHours(-24);

        var latest = await db.RiskEvaluations
            .IgnoreQueryFilters()
            .Where(e => e.EvaluatedAtUtc >= since)
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, Peak = g.Max(e => e.TotalScore) })
            .Take(_batchSize)
            .ToListAsync(ct);

        if (latest.Count == 0) return;

        var userIds = latest.Select(x => x.UserId).ToList();
        var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToListAsync(ct);

        var changed = 0;
        foreach (var entry in latest)
        {
            var user = users.FirstOrDefault(u => u.Id == entry.UserId);
            if (user is null) continue;

            var level = entry.Peak switch
            {
                >= 80 => "CRITICAL",
                >= 60 => "HIGH",
                >= 30 => "MEDIUM",
                _ => "LOW"
            };

            if (user.RiskLevel == level) continue;

            user.RiskLevel = level;
            changed++;

            if (level == "CRITICAL")
            {
                var dedupe = $"critical-risk:{user.Id}:{DateTimeOffset.UtcNow:yyyyMMddHH}";
                var exists = await db.SecurityEvents.IgnoreQueryFilters()
                    .AnyAsync(e => e.DedupeKey == dedupe, ct);

                if (!exists)
                {
                    db.SecurityEvents.Add(new SecurityEvent
                    {
                        OrganizationId = user.OrganizationId,
                        Detector = "CRITICAL_RISK_USER",
                        Severity = "CRITICAL",
                        Title = $"User reached CRITICAL risk: {user.DisplayName}",
                        Detail = $"Peak risk score {entry.Peak} in the last 24 hours.",
                        SubjectUserId = user.Id,
                        DedupeKey = dedupe
                    });
                }
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Updated risk level for {Count} user(s).", changed);
        }
    }
}
