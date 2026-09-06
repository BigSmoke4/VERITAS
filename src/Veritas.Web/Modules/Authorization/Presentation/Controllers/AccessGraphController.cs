using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.Authorization.Application;

namespace Veritas.Web.Modules.Authorization.Presentation.Controllers;

[ApiController]
[Route("api/v1/access-graph")]
[Authorize]
public sealed class AccessGraphController : ControllerBase
{
    private readonly IAccessGraphQueryService _graph;
    public AccessGraphController(IAccessGraphQueryService graph) => _graph = graph;

    /// <summary>
    /// "What can this user access?" — walks real UserRole -> RolePermission
    /// -> Permission edges from the database via the shared
    /// AccessGraphQueryService (also used by the Razor UI, so the two never
    /// diverge). No synthetic/sample nodes are ever added; a user with zero
    /// roles simply returns a single node.
    /// </summary>
    [HttpGet("user/{userId:guid}")]
    public async Task<ActionResult<AccessGraphDto>> ForUser(Guid userId, CancellationToken ct)
    {
        var graph = await _graph.BuildForUserAsync(userId, ct);
        return graph is null ? NotFound() : Ok(graph);
    }

    /// <summary>"Who can access this resource?" — resolved from active
    /// TemporaryGrant rows (real JIT/privileged grants). Broader
    /// policy-derived reachability would require enumerating all users
    /// through the Policy Simulator's evaluator per spec section 24; this
    /// endpoint intentionally covers only what can be answered directly from
    /// stored grants rather than approximating the rest.</summary>
    [HttpGet("resource/{resourceId:guid}")]
    public async Task<ActionResult<AccessGraphDto>> ForResource(Guid resourceId, CancellationToken ct)
    {
        var graph = await _graph.BuildForResourceAsync(resourceId, ct);
        return graph is null ? NotFound() : Ok(graph);
    }
}
