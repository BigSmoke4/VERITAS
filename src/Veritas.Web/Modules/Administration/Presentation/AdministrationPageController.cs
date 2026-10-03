using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.Administration.Application;
using Veritas.Web.Modules.RoleManagement.Application;

namespace Veritas.Web.Modules.Administration.Presentation;

public sealed class AdministrationIndexViewModel
{
    public required SystemStatus Status { get; init; }
    public required IReadOnlyList<SoDRuleSummary> SoDRules { get; init; }
    public IReadOnlyList<string> Endpoints { get; init; } = Array.Empty<string>();
}

[Authorize]
public sealed class AdministrationPageController : Controller
{
    private readonly ISystemStatusService _status;
    private readonly IRoleService _roles;

    public AdministrationPageController(ISystemStatusService status, IRoleService roles)
    {
        _status = status;
        _roles = roles;
    }

    public async Task<IActionResult> Index(CancellationToken ct) => View(new AdministrationIndexViewModel
    {
        Status = await _status.GetAsync(ct),
        SoDRules = await _roles.ListSoDRulesAsync(ct),
        Endpoints = new[]
        {
            "POST /api/v1/authorize — zero-trust authorization decision",
            "GET  /api/v1/access-graph/user/{id} — what a subject can reach",
            "GET  /api/v1/access-graph/resource/{id} — who can reach a resource",
            "POST /api/v1/access-requests/{id}/approvals — approval decision",
            "POST /api/v1/privileged-access/grants — issue a JIT grant",
            "GET  /api/v1/access-review/items/{id}/decision — record a review decision",
            "GET  /swagger — OpenAPI document",
            "GET  /health, /health/live, /health/ready — health probes"
        }
    });
}
