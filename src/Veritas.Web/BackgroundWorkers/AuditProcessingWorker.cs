using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Post-processes the audit stream: reconciles granted AccessRequests whose
/// expiry has passed (so the request list reflects reality even if the grant
/// worker and this one run at different times) and records the reconciliation
/// as an audit event. Idempotent — it only touches rows still marked Granted.
/// </summary>
public sealed class AuditProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditProcessingWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public AuditProcessingWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AuditProcessingWorker> logger,
        IOptions<WorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = options.Value.SecurityDetectionPollInterval;
        _batchSize = options.Value.BatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AuditProcessingWorker tick failed; will retry.");
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

        var expiredRequests = await db.AccessRequests
            .IgnoreQueryFilters()
            .Where(r => r.Status == AccessRequest.Domain.AccessRequestStatus.Granted
                        && r.ExpiresAtUtc != null && r.ExpiresAtUtc <= now)
            .Take(_batchSize)
            .ToListAsync(ct);

        if (expiredRequests.Count == 0) return;

        foreach (var request in expiredRequests)
        {
            request.Status = AccessRequest.Domain.AccessRequestStatus.Expired;

            db.AuditLogs.Add(new AuditLog
            {
                OrganizationId = request.OrganizationId,
                Action = "ACCESS_GRANT_EXPIRED",
                ResourceId = request.ResourceId.ToString(),
                PreviousValue = "GRANTED",
                NewValue = "EXPIRED",
                CorrelationId = request.Id.ToString()
            });
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Marked {Count} expired access request(s).", expiredRequests.Count);
    }
}
