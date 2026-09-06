using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Modules.PolicyManagement.Application;

namespace Veritas.Web.Modules.PolicyManagement.Presentation.Controllers;

public sealed record SimulatePolicyRequestDto(Guid CandidatePolicyVersionId, Guid ResourceId, string Action);

[ApiController]
[Route("api/v1/policies/simulate")]
[Authorize]
[EnableRateLimiting("admin-api")]
public sealed class PolicySimulatorController : ControllerBase
{
    private readonly IPolicySimulatorService _simulator;
    private readonly IValidator<SimulatePolicyRequestDto> _validator;

    public PolicySimulatorController(IPolicySimulatorService simulator, IValidator<SimulatePolicyRequestDto> validator)
    {
        _simulator = simulator;
        _validator = validator;
    }

    /// <summary>
    /// Runs the real policy evaluator across every real current user for the
    /// given resource/action, comparing today's Published policy set against
    /// the candidate (unpublished) version. Must be run before a policy is
    /// allowed to move to Published (spec section 75 — publish gate is
    /// enforced by the caller/UI, not bypassed here).
    /// </summary>
    /// <remarks>
    /// Example request:
    ///
    ///     POST /api/v1/policies/simulate
    ///     { "candidatePolicyVersionId": "3f5b1e2a-...-v8", "resourceId": "3f5b1e2a-...", "action": "read" }
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(PolicySimulationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PolicySimulationResult>> Simulate([FromBody] SimulatePolicyRequestDto dto, CancellationToken ct)
    {
        var validation = await _validator.ValidateAsync(dto, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            return ValidationProblem(ModelState);
        }

        var result = await _simulator.SimulateAsync(dto.CandidatePolicyVersionId, dto.ResourceId, dto.Action, ct);
        return Ok(result);
    }
}
