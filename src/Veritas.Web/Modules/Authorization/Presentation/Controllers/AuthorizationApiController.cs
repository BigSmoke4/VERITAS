using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Infrastructure.Idempotency;
using System.Text.Json;

namespace Veritas.Web.Modules.Authorization.Presentation.Controllers;

public sealed record AuthorizeRequestDto(
    string Subject,
    string Resource,
    string Action,
    string Environment,
    AuthorizeContextDto? Context);

public sealed record AuthorizeContextDto(string? Ip, string? DeviceTrust, string? AuthenticationStrength);

public sealed record AuthorizeResponseDto(
    string Decision,
    string DecisionId,
    string? PolicyVersion,
    int? RiskScore,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Reasons);

[ApiController]
[Route("api/v1/authorize")]
[Authorize]
[EnableRateLimiting("authorization-api")]
public sealed class AuthorizationApiController : ControllerBase
{
    private readonly IAuthorizationService _authorizationService;
    private readonly IIdempotencyService _idempotency;
    private readonly IValidator<AuthorizeRequestDto> _validator;

    public AuthorizationApiController(IAuthorizationService authorizationService, IIdempotencyService idempotency, IValidator<AuthorizeRequestDto> validator)
    {
        _authorizationService = authorizationService;
        _idempotency = idempotency;
        _validator = validator;
    }

    /// <summary>
    /// The platform's core primitive. Every decision is persisted with the
    /// exact policy version it matched, so it can be reproduced later even
    /// after subsequent policy versions are published.
    /// </summary>
    /// <remarks>
    /// Example request:
    ///
    ///     POST /api/v1/authorize
    ///     Idempotency-Key: 5c1e2f3a-example
    ///     {
    ///       "subject": "3f5b1e2a-1111-4c2a-9a4e-000000000001",
    ///       "resource": "3f5b1e2a-1111-4c2a-9a4e-000000000002",
    ///       "action": "read",
    ///       "environment": "production",
    ///       "context": { "ip": "10.20.10.12", "deviceTrust": "high", "authenticationStrength": "strong" }
    ///     }
    ///
    /// Example response:
    ///
    ///     { "decision": "ALLOW", "decisionId": "DEC-9283721...", "policyVersion": "...-v3", "riskScore": 18, "expiresAt": null, "reasons": ["Matched rule 1 on policy version 3 -> Allow"] }
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(AuthorizeResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthorizeResponseDto>> Authorize(
        [FromBody] AuthorizeRequestDto dto,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var validation = await _validator.ValidateAsync(dto, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            return ValidationProblem(ModelState);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var cached = await _idempotency.TryGetCachedResponseAsync(idempotencyKey, ct);
            if (cached is not null)
            {
                var cachedResponse = JsonSerializer.Deserialize<AuthorizeResponseDto>(cached);
                return Ok(cachedResponse);
            }
        }

        var request = new AuthorizationRequest
        {
            SubjectUserId = dto.Subject,
            ResourceId = dto.Resource,
            Action = dto.Action,
            Environment = dto.Environment,
            Ip = dto.Context?.Ip,
            DeviceTrust = dto.Context?.DeviceTrust,
            AuthenticationStrength = dto.Context?.AuthenticationStrength,
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N")
        };

        var outcome = await _authorizationService.AuthorizeAsync(request, ct);

        var response = new AuthorizeResponseDto(
            Decision: outcome.Result.ToString().ToUpperInvariant(),
            DecisionId: $"DEC-{outcome.DecisionId:N}",
            PolicyVersion: outcome.MatchedRule is null
                ? null
                : $"{outcome.MatchedRule.PolicyId}-v{outcome.MatchedRule.PolicyVersionNumber}",
            RiskScore: outcome.RiskScore,
            ExpiresAt: outcome.ExpiresAtUtc,
            Reasons: outcome.Reasons);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _idempotency.StoreResponseAsync(idempotencyKey, JsonSerializer.Serialize(response), TimeSpan.FromMinutes(10), ct);
        }

        return Ok(response);
    }
}
