using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Polls TemporaryGrant rows for anything past ExpiresAtUtc and marks it
/// Revoked. Deliberately DB-driven (no Timer/Task.Delay keyed to a specific
/// grant's expiry) so that if the app restarts or runs as N replicas, no
/// grant can silently survive past its expiration — the next poll from any
/// instance will catch it. Uses a fresh DI scope (and therefore a fresh
/// DbContext) per tick, per spec section 35.
/// </summary>
public sealed class TemporaryAccessExpirationWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TemporaryAccessExpirationWorker> _logger;

    public TemporaryAccessExpirationWorker(IServiceScopeFactory scopeFactory, ILogger<TemporaryAccessExpirationWorker> logger)
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
                await ExpireDueGrantsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let a single failed tick kill the worker loop — log and retry next interval.
                _logger.LogError(ex, "TemporaryAccessExpirationWorker tick failed; will retry.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ExpireDueGrantsAsync(CancellationToken ct)
    {
        // Note: workers run outside any HTTP request, so there's no ambient
        // tenant claim for IAuditService/ITenantContext to resolve — we write
        // AuditLog rows directly here, stamped with each grant's own
        // OrganizationId, rather than going through the HTTP-claim-based
        // IAuditService which would silently record OrganizationId = Guid.Empty.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();

        var now = DateTimeOffset.UtcNow;

        // Idempotent by construction: the WHERE clause only ever matches grants
        // that are still un-revoked and past expiry, so re-running this on the
        // same row twice (e.g. after a crash mid-batch) is a no-op the second time.
        var due = await db.Set<TemporaryGrant>()
            .IgnoreQueryFilters() // workers act across all tenants
            .Where(g => !g.Revoked && g.ExpiresAtUtc <= now)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var grant in due)
        {
            grant.Revoked = true;
            grant.RevokedAtUtc = now;
            grant.RevokedReason = "Automatic expiration.";
        }

        foreach (var grant in due)
        {
            db.AuditLogs.Add(new AuditLog
            {
                OrganizationId = grant.OrganizationId,
                ActorUserId = null,
                Action = "TEMPORARY_ACCESS_EXPIRED",
                ResourceId = grant.ResourceId.ToString(),
                PreviousValue = "ACTIVE",
                NewValue = "EXPIRED",
                CorrelationId = grant.Id.ToString()
            });
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Expired {Count} temporary grant(s).", due.Count);
    }
}
