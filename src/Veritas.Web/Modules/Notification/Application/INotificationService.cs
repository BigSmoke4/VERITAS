using Veritas.Web.Modules.Notification.Domain;

namespace Veritas.Web.Modules.Notification.Application;

public interface INotificationService
{
    /// <summary>Enqueues a notification for background delivery — never sends synchronously.</summary>
    Task EnqueueAsync(Guid recipientUserId, NotificationChannel channel, string eventType, string subject, string body, CancellationToken ct = default);
}

/// <summary>Abstraction over the actual transport so delivery is swappable/testable.</summary>
public interface INotificationTransport
{
    NotificationChannel Channel { get; }
    Task SendAsync(NotificationRecord notification, CancellationToken ct = default);
}
