using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.Notification.Domain;

public enum NotificationChannel { Email, InApp, Webhook }
public enum NotificationStatus { Pending, Sent, Failed }

/// <summary>
/// Real outbox pattern: notifications are written here inside the same
/// transaction as the triggering event, then a background worker delivers
/// them. This decouples request latency from delivery (spec section 32:
/// "never block the main request on email delivery") without losing events
/// if the process crashes between "decision made" and "email sent".
/// </summary>
public class NotificationRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid RecipientUserId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string EventType { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string Body { get; set; } = default!;
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAtUtc { get; set; }
}

/// <summary>
/// A real, persisted webhook subscription per tenant/event-type. Previously
/// WebhookNotificationTransport had nowhere to look up a URL and just logged
/// "not implemented" — this is the store that was missing.
/// </summary>
public class WebhookSubscription : Veritas.Web.Shared.Domain.ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string EventType { get; set; } = default!; // e.g. "ACCESS_APPROVED", "*" for all events
    public string TargetUrl { get; set; } = default!;
    public string? SecretForSigning { get; set; } // used to HMAC-sign the payload, never logged
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
