using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Notification.Application;

public sealed class NotificationService : INotificationService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;

    public NotificationService(VeritasDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task EnqueueAsync(Guid recipientUserId, NotificationChannel channel, string eventType, string subject, string body, CancellationToken ct = default)
    {
        _db.Set<NotificationRecord>().Add(new NotificationRecord
        {
            OrganizationId = _tenant.OrganizationId,
            RecipientUserId = recipientUserId,
            Channel = channel,
            EventType = eventType,
            Subject = subject,
            Body = body
        });
        await _db.SaveChangesAsync(ct);
    }
}
