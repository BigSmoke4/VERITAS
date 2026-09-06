using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Notification.Domain;

namespace Veritas.Web.Modules.Notification.Application;

/// <summary>
/// Real, working transport that logs the outbound email rather than actually
/// calling an SMTP server — because no SMTP credentials exist in this
/// environment. The delivery pipeline (outbox -> worker -> transport
/// interface -> retry/failure tracking) is fully real; swapping this for
/// MailKit/SendGrid/etc. is a one-file change behind INotificationTransport.
/// </summary>
public sealed class LoggingEmailTransport : INotificationTransport
{
    private readonly ILogger<LoggingEmailTransport> _logger;
    public LoggingEmailTransport(ILogger<LoggingEmailTransport> logger) => _logger = logger;

    public NotificationChannel Channel => NotificationChannel.Email;

    public Task SendAsync(NotificationRecord notification, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "EMAIL DELIVERY (logging transport — configure a real SMTP/API transport for production): To user {UserId}, Subject: {Subject}\n{Body}",
            notification.RecipientUserId, notification.Subject, notification.Body);
        return Task.CompletedTask;
    }
}

/// <summary>In-app notifications are "delivered" by simply marking them Sent —
/// the Razor UI reads NotificationRecord rows directly for the current user.</summary>
public sealed class InAppNotificationTransport : INotificationTransport
{
    public NotificationChannel Channel => NotificationChannel.InApp;
    public Task SendAsync(NotificationRecord notification, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Real webhook delivery: looks up enabled WebhookSubscription rows matching
/// this event type (or a "*" wildcard subscription) for the tenant, POSTs
/// the event as JSON, and HMAC-SHA256-signs the payload with the
/// subscription's secret (sent as the X-Veritas-Signature header) so the
/// receiver can verify authenticity. If no subscription exists for this
/// event/tenant, that's a normal no-op — not every event needs a webhook.
/// </summary>
public sealed class WebhookNotificationTransport : INotificationTransport
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookNotificationTransport> _logger;
    private readonly Veritas.Web.Shared.Infrastructure.VeritasDbContext _db;

    public WebhookNotificationTransport(
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookNotificationTransport> logger,
        Veritas.Web.Shared.Infrastructure.VeritasDbContext db)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _db = db;
    }

    public NotificationChannel Channel => NotificationChannel.Webhook;

    public async Task SendAsync(NotificationRecord notification, CancellationToken ct = default)
    {
        var subscriptions = await _db.Set<WebhookSubscription>()
            .Where(s => s.Enabled && s.OrganizationId == notification.OrganizationId
                     && (s.EventType == notification.EventType || s.EventType == "*"))
            .ToListAsync(ct);

        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("No webhook subscription registered for event {EventType} in org {OrganizationId}; skipping.",
                notification.EventType, notification.OrganizationId);
            return;
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            eventType = notification.EventType,
            subject = notification.Subject,
            body = notification.Body,
            recipientUserId = notification.RecipientUserId,
            occurredAtUtc = notification.CreatedAtUtc
        });

        var client = _httpClientFactory.CreateClient(nameof(WebhookNotificationTransport));

        foreach (var subscription in subscriptions)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.TargetUrl)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrEmpty(subscription.SecretForSigning))
            {
                var signature = ComputeSignature(payload, subscription.SecretForSigning);
                request.Headers.Add("X-Veritas-Signature", signature);
            }

            try
            {
                var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Webhook delivery to {Url} returned {StatusCode} for event {EventType}.",
                        subscription.TargetUrl, response.StatusCode, notification.EventType);
                    throw new InvalidOperationException($"Webhook endpoint returned {response.StatusCode}.");
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Webhook delivery to {Url} failed.", subscription.TargetUrl);
                throw; // let NotificationWorker's retry/attempt-count logic handle it
            }
        }
    }

    private static string ComputeSignature(string payload, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }
}
