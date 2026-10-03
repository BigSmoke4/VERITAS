using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.RiskManagement.Application;

namespace Veritas.Web.Modules.RiskManagement.Presentation;

[Authorize]
public sealed class RiskPageController : Controller
{
    private readonly IRiskDashboardService _dashboard;
    public RiskPageController(IRiskDashboardService dashboard) => _dashboard = dashboard;

    public async Task<IActionResult> Index(CancellationToken ct) => View(await _dashboard.GetAsync(ct));
}
