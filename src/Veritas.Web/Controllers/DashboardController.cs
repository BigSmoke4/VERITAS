using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.Analytics.Application;

namespace Veritas.Web.Controllers;

[Authorize]
public sealed class DashboardController : Controller
{
    private readonly IControlCenterService _controlCenter;
    public DashboardController(IControlCenterService controlCenter) => _controlCenter = controlCenter;

    public async Task<IActionResult> Index(CancellationToken ct) => View(await _controlCenter.GetSnapshotAsync(ct));
}
