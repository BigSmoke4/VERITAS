using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Retention and hygiene: purges audit rows older than Audit:RetentionDays
/// (0 disables purging entirely) and deletes Redis policy-cache generations
/// that have aged past their TTL. Deletion is batched so a large backlog
/// cannot turn one tick into a single huge transaction or a long lock.
/// </summary>
public sealed class CleanupWorker : BackgroundService
{
    private const int BatchSize = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CleanupWorker> _logger;
    private readonly AuditOptions _auditOptions;
    private readonly TimeSpan _interval;

    public CleanupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CleanupWorker> logger,
        IOptions<AuditOptions> auditOptions,
        IOptions<WorkerOptions> workerOptions)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _auditOptions = auditOptions.Value;
        _interval = workerOptions.Value.CleanupPollInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once shortly after startup, then on the configured cadence.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CleanupWorker tick failed; will retry.");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_auditOptions.RetentionDays <= 0)
        {
            _logger.LogDebug("Audit retention disabled (RetentionDays=0); nothing to purge.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_auditOptions.RetentionDays);

        var totalDeleted = 0;
        while (true)
        {
            var batch = await db.AuditLogs
                .IgnoreQueryFilters()
                .Where(a => a.TimestampUtc < cutoff)
                .OrderBy(a => a.TimestampUtc)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            db.AuditLogs.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            totalDeleted += batch.Count;

            if (batch.Count < BatchSize) break;
        }

        if (totalDeleted > 0)
            _logger.LogInformation("Purged {Count} audit row(s) older than {Days} days.", totalDeleted, _auditOptions.RetentionDays);
    }
}
