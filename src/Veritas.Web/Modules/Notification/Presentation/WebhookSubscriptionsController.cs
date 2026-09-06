using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Notification.Presentation;

public sealed record CreateWebhookSubscriptionDto(string EventType, string TargetUrl, string? SecretForSigning);

public sealed record WebhookSubscriptionResponse(
    Guid Id,
    string EventType,
    string TargetUrl,
    bool Enabled,
    DateTimeOffset CreatedAtUtc);

public sealed class CreateWebhookSubscriptionDtoValidator : AbstractValidator<CreateWebhookSubscriptionDto>
{
    public CreateWebhookSubscriptionDtoValidator()
    {
        RuleFor(x => x.EventType).NotEmpty().MaximumLength(64);
        RuleFor(x => x.TargetUrl).NotEmpty().Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            .WithMessage("TargetUrl must be a valid https:// URL.");
    }
}

/// <summary>Real CRUD over WebhookSubscription — this is what WebhookNotificationTransport reads from.</summary>
[ApiController]
[Route("api/v1/webhook-subscriptions")]
[Authorize]
[EnableRateLimiting("admin-api")]
public sealed class WebhookSubscriptionsController : ControllerBase
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IValidator<CreateWebhookSubscriptionDto> _validator;

    public WebhookSubscriptionsController(VeritasDbContext db, ITenantContext tenant, IValidator<CreateWebhookSubscriptionDto> validator)
    {
        _db = db;
        _tenant = tenant;
        _validator = validator;
    }

    /// <summary>Lists webhook subscriptions for the authenticated tenant.</summary>
    /// <remarks>
    /// Example response (the signing secret is intentionally never returned):
    ///
    ///     [
    ///       {
    ///         "id": "11111111-1111-1111-1111-111111111111",
    ///         "eventType": "ACCESS_APPROVED",
    ///         "targetUrl": "https://example.com/hooks/veritas",
    ///         "enabled": true,
    ///         "createdAtUtc": "2026-09-06T05:30:00Z"
    ///       }
    ///     ]
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(List<WebhookSubscriptionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<WebhookSubscriptionResponse>>> List(CancellationToken ct)
    {
        var subscriptions = await _db.WebhookSubscriptions
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new WebhookSubscriptionResponse(s.Id, s.EventType, s.TargetUrl, s.Enabled, s.CreatedAtUtc))
            .ToListAsync(ct);

        return Ok(subscriptions);
    }

    /// <remarks>
    /// Example request:
    ///
    ///     POST /api/v1/webhook-subscriptions
    ///     { "eventType": "ACCESS_APPROVED", "targetUrl": "https://example.com/hooks/veritas", "secretForSigning": "a-shared-secret" }
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWebhookSubscriptionDto dto, CancellationToken ct)
    {
        var validation = await _validator.ValidateAsync(dto, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors) ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            return ValidationProblem(ModelState);
        }

        var subscription = new WebhookSubscription
        {
            OrganizationId = _tenant.OrganizationId,
            EventType = dto.EventType,
            TargetUrl = dto.TargetUrl,
            SecretForSigning = dto.SecretForSigning
        };
        _db.WebhookSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(ct);
        return Ok(new { subscription.Id });
    }

    /// <summary>Deletes one webhook subscription from the authenticated tenant.</summary>
    /// <remarks>
    /// Example request:
    ///
    ///     DELETE /api/v1/webhook-subscriptions/11111111-1111-1111-1111-111111111111
    ///
    /// Successful response: HTTP 204 No Content.
    ///
    /// If the id does not belong to the current tenant (or does not exist), the response is HTTP 404.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var sub = await _db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub is null) return NotFound();
        _db.WebhookSubscriptions.Remove(sub);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
