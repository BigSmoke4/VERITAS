using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Modules.PrivilegedAccess.Application;

namespace Veritas.Web.Modules.PrivilegedAccess.Presentation;

public sealed record GrantTemporaryAccessDto(Guid UserId, Guid ResourceId, string PermissionKey, int DurationMinutes, string Reason);

[ApiController]
[Route("api/v1/privileged-access")]
[Authorize]
[EnableRateLimiting("admin-api")]
public sealed class PrivilegedAccessController : ControllerBase
{
    private readonly IPrivilegedAccessService _service;
    private readonly IValidator<GrantTemporaryAccessDto> _validator;

    public PrivilegedAccessController(IPrivilegedAccessService service, IValidator<GrantTemporaryAccessDto> validator)
    {
        _service = service;
        _validator = validator;
    }

    /// <remarks>
    /// Example request:
    ///
    ///     POST /api/v1/privileged-access/grant
    ///     { "userId": "...", "resourceId": "...", "permissionKey": "payment.read", "durationMinutes": 30, "reason": "Incident INC-20482" }
    /// </remarks>
    [HttpPost("grant")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Grant([FromBody] GrantTemporaryAccessDto dto, CancellationToken ct)
    {
        var validation = await _validator.ValidateAsync(dto, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            return ValidationProblem(ModelState);
        }

        var grant = await _service.GrantTemporaryAccessAsync(
            dto.UserId, dto.ResourceId, dto.PermissionKey, TimeSpan.FromMinutes(dto.DurationMinutes), dto.Reason, ct);
        return Ok(new { grantId = grant.Id, expiresAtUtc = grant.ExpiresAtUtc });
    }

    [HttpPost("{grantId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid grantId, [FromBody] string reason, CancellationToken ct)
    {
        await _service.RevokeAsync(grantId, reason, ct);
        return NoContent();
    }
}
