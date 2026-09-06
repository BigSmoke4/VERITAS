using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Notification.Application;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.BackgroundWorkers;

/// <summary>
/// Delivers Pending NotificationRecord rows via the transport matching their
/// Channel. Failures increment AttemptCount and store LastError rather than
/// throwing the whole batch away — a notification is retried up to
/// MaxAttempts times, then left Failed for an operator to inspect, never
/// silently dropped.
/// </summary>
public sealed class NotificationWorker : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationWorker> _logger;

    public NotificationWorker(IServiceScopeFactory scopeFactory, ILogger<NotificationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverPendingAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "NotificationWorker tick failed; will retry.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DeliverPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var transports = scope.ServiceProvider.GetServices<INotificationTransport>().ToDictionary(t => t.Channel);

        var pending = await db.Set<NotificationRecord>()
            .IgnoreQueryFilters()
            .Where(n => n.Status == NotificationStatus.Pending && n.AttemptCount < MaxAttempts)
            .OrderBy(n => n.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        foreach (var notification in pending)
        {
            notification.AttemptCount++;
            try
            {
                if (transports.TryGetValue(notification.Channel, out var transport))
                {
                    await transport.SendAsync(notification, ct);
                    notification.Status = NotificationStatus.Sent;
                    notification.SentAtUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    notification.LastError = $"No transport registered for channel {notification.Channel}.";
                    if (notification.AttemptCount >= MaxAttempts) notification.Status = NotificationStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                notification.LastError = ex.Message;
                if (notification.AttemptCount >= MaxAttempts) notification.Status = NotificationStatus.Failed;
                _logger.LogWarning(ex, "Notification {Id} delivery attempt {Attempt} failed.", notification.Id, notification.AttemptCount);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
