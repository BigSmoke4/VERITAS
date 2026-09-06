using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Modules.ApplicationRegistry.Application;

namespace Veritas.Web.Modules.ApplicationRegistry.Presentation;

public sealed record CreateApiKeyDto(Guid ServiceAccountId, List<string> Scopes, int? TtlHours);

[ApiController]
[Route("api/v1/service-accounts/{serviceAccountId:guid}/api-keys")]
[Authorize]
[EnableRateLimiting("admin-api")]
public sealed class ApiKeysController : ControllerBase
{
    private readonly IApiKeyService _apiKeys;
    private readonly IValidator<CreateApiKeyDto> _validator;

    public ApiKeysController(IApiKeyService apiKeys, IValidator<CreateApiKeyDto> validator)
    {
        _apiKeys = apiKeys;
        _validator = validator;
    }

    /// <summary>Returns the plaintext secret exactly once, in this response only — it is never stored or retrievable again (spec section 28).</summary>
    /// <remarks>
    /// Example request:
    ///
    ///     POST /api/v1/service-accounts/{serviceAccountId}/api-keys
    ///     { "serviceAccountId": "...", "scopes": ["payment.read"], "ttlHours": 720 }
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(Guid serviceAccountId, [FromBody] CreateApiKeyDto dto, CancellationToken ct)
    {
        var validation = await _validator.ValidateAsync(dto, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            return ValidationProblem(ModelState);
        }

        var ttl = dto.TtlHours.HasValue ? TimeSpan.FromHours(dto.TtlHours.Value) : (TimeSpan?)null;
        var created = await _apiKeys.CreateAsync(serviceAccountId, dto.Scopes, ttl, ct);
        return Ok(new { apiKeyId = created.ApiKeyId, secret = created.PlaintextSecret, keyPrefix = created.KeyPrefix,
            warning = "Store this secret now — it will never be shown again." });
    }

    [HttpPost("{apiKeyId:guid}/rotate")]
    public async Task<IActionResult> Rotate(Guid apiKeyId, CancellationToken ct)
    {
        var rotated = await _apiKeys.RotateAsync(apiKeyId, ct);
        return Ok(new { apiKeyId = rotated.ApiKeyId, secret = rotated.PlaintextSecret, keyPrefix = rotated.KeyPrefix });
    }

    [HttpDelete("{apiKeyId:guid}")]
    public async Task<IActionResult> Revoke(Guid apiKeyId, CancellationToken ct)
    {
        await _apiKeys.RevokeAsync(apiKeyId, ct);
        return NoContent();
    }
}
