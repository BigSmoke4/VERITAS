using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.PolicyManagement.Application;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.PolicyManagement.Presentation;

public sealed class PolicySimulatorFormViewModel
{
    public List<(Guid Id, string Label)> DraftOrReviewVersions { get; init; } = new();
    public List<(Guid Id, string Name)> Resources { get; init; } = new();
    public PolicySimulationResult? Result { get; init; }
    public string? ErrorMessage { get; init; }
}

[Authorize]
public sealed class PolicySimulatorPageController : Controller
{
    private readonly VeritasDbContext _db;
    private readonly IPolicySimulatorService _simulator;

    public PolicySimulatorPageController(VeritasDbContext db, IPolicySimulatorService simulator)
    {
        _db = db;
        _simulator = simulator;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = await BuildFormAsync(null, ct);
        return View("~/Views/PolicySimulator/Index.cshtml", vm);
    }

    /// <summary>
    /// Runs the real Policy Simulator (same engine as production authorize
    /// calls) and re-renders the same page with the actual result — nothing
    /// here is placeholder or interpolated data.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(Guid candidatePolicyVersionId, Guid resourceId, string action, CancellationToken ct)
    {
        try
        {
            var result = await _simulator.SimulateAsync(candidatePolicyVersionId, resourceId, action, ct);
            var vm = await BuildFormAsync(result, ct);
            return View("~/Views/PolicySimulator/Index.cshtml", vm);
        }
        catch (InvalidOperationException ex)
        {
            var vm = await BuildFormAsync(null, ct);
            var errored = new PolicySimulatorFormViewModel
            {
                DraftOrReviewVersions = vm.DraftOrReviewVersions,
                Resources = vm.Resources,
                ErrorMessage = ex.Message
            };
            return View("~/Views/PolicySimulator/Index.cshtml", errored);
        }
    }

    private async Task<PolicySimulatorFormViewModel> BuildFormAsync(PolicySimulationResult? result, CancellationToken ct)
    {
        var versions = await _db.PolicyVersions
            .Include(v => v.Policy)
            .Where(v => v.Status == Domain.PolicyLifecycleStatus.Draft || v.Status == Domain.PolicyLifecycleStatus.Review)
            .ToListAsync(ct);

        var resources = await _db.Resources.ToListAsync(ct);

        return new PolicySimulatorFormViewModel
        {
            DraftOrReviewVersions = versions.Select(v => (v.Id, $"{v.Policy?.Name ?? v.PolicyId.ToString()} v{v.VersionNumber} ({v.Status})")).ToList(),
            Resources = resources.Select(r => (r.Id, r.Name)).ToList(),
            Result = result
        };
    }
}
