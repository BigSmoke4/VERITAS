using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veritas.Web.Modules.PolicyManagement.Application;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Modules.ResourceManagement.Application;

namespace Veritas.Web.Modules.PolicyManagement.Presentation;

public sealed class PoliciesIndexViewModel
{
    public IReadOnlyList<PolicySummary> Policies { get; init; } = Array.Empty<PolicySummary>();
}

public sealed class PolicyDetailsViewModel
{
    public required Policy Policy { get; init; }
    public required IReadOnlyList<PolicyVersionDetail> Versions { get; init; }
    public PolicyVersionDetail? Selected { get; init; }
}

public sealed class PolicyEditViewModel
{
    public required PolicyVersionDetail Version { get; init; }
    public IReadOnlyList<ResourceSummary> Resources { get; init; } = Array.Empty<ResourceSummary>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Policy Studio (spec section 47). Building a rule posts structured
/// attribute/operator/value triples — never expression text — so there is no
/// code path in which policy input is compiled or eval'd.
/// </summary>
[Authorize]
public sealed class PoliciesPageController : Controller
{
    private static readonly string[] KnownAttributes =
    {
        "user.department", "user.status", "user.riskLevel", "user.clearance", "user.roles",
        "resource.classification", "resource.department", "resource.environment", "resource.type",
        "application.environment",
        "request.action", "request.environment", "request.deviceTrust",
        "request.authenticationStrength", "request.riskLevel"
    };

    private readonly IPolicyManagementService _policies;
    private readonly IResourceService _resources;

    public PoliciesPageController(IPolicyManagementService policies, IResourceService resources)
    {
        _policies = policies;
        _resources = resources;
    }

    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new PoliciesIndexViewModel { Policies = await _policies.ListAsync(ct) });

    public async Task<IActionResult> Details(Guid id, Guid? versionId, CancellationToken ct)
    {
        var policy = await _policies.GetPolicyAsync(id, ct);
        if (policy is null) return NotFound();

        var versions = new List<PolicyVersionDetail>();
        foreach (var version in policy.Versions.OrderByDescending(v => v.VersionNumber))
            versions.Add((await _policies.GetVersionAsync(version.Id, ct))!);

        PolicyVersionDetail? selected = null;
        if (versionId is not null)
            selected = versions.FirstOrDefault(v => v.Id == versionId);
        selected ??= versions.FirstOrDefault(v => v.Status == PolicyLifecycleStatus.Published) ?? versions.FirstOrDefault();

        return View(new PolicyDetailsViewModel { Policy = policy, Versions = versions, Selected = selected });
    }

    public async Task<IActionResult> Edit(Guid versionId, CancellationToken ct)
    {
        var version = await _policies.GetVersionAsync(versionId, ct);
        if (version is null) return NotFound();

        return View(new PolicyEditViewModel
        {
            Version = version,
            Resources = await _resources.ListResourcesAsync(null, null, ct)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string owner, CancellationToken ct)
    {
        var policy = await _policies.CreatePolicyAsync(name, owner, ct);
        TempData["veritas.notice"] = $"Policy '{policy.Name}' created. Add a draft version to define rules.";
        return RedirectToAction(nameof(Details), new { id = policy.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDraftVersion(Guid policyId, CancellationToken ct)
    {
        var version = await _policies.CreateDraftVersionAsync(policyId, Array.Empty<PolicyRuleDraft>(), ct);
        TempData["veritas.notice"] = $"Draft v{version.VersionNumber} created.";
        return RedirectToAction(nameof(Edit), new { versionId = version.Id });
    }

    /// <summary>
    /// Accepts parallel arrays from the structured builder: one `ruleOrder` /
    /// `rulePriority` / `ruleEffect` entry per rule card, and one
    /// `ruleIndex` / `conditionAttribute` / `conditionOperator` / `conditionValue` /
    /// `conditionIsRef` entry per condition. Each condition is grouped under the rule
    /// whose index it carries.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRules(
        Guid versionId,
        int[] ruleOrder, int[] ruleIndex, int[] rulePriority, string[] ruleEffect,
        string[] conditionAttribute, string[] conditionOperator, string[] conditionValue, bool[] conditionIsRef,
        CancellationToken ct)
    {
        var version = await _policies.GetVersionAsync(versionId, ct);
        if (version is null) return NotFound();

        var drafts = new List<PolicyRuleDraft>();
        try
        {
            // `ruleOrder` carries one marker per rule card, in order, so a rule with zero
            // conditions still survives the round trip. `ruleIndex` carries one marker per
            // condition and says which rule card that condition belongs to. The two are
            // deliberately separate: a single array could not express an empty rule.
            var ruleCount = ruleOrder?.Length ?? 0;
            for (var r = 0; r < ruleCount; r++)
            {
                var conditions = new List<PolicyConditionDraft>();
                for (var c = 0; c < (conditionAttribute?.Length ?? 0); c++)
                {
                    if (ruleIndex is null || ruleIndex.Length <= c || ruleIndex[c] != r) continue;
                    if (string.IsNullOrWhiteSpace(conditionAttribute![c])) continue;

                    conditions.Add(new PolicyConditionDraft(
                        conditionAttribute[c],
                        ParseOperator(conditionOperator is not null && conditionOperator.Length > c
                            ? conditionOperator[c] : "Equals"),
                        conditionValue is not null && conditionValue.Length > c
                            ? conditionValue[c] ?? string.Empty : string.Empty,
                        conditionIsRef is not null && conditionIsRef.Length > c && conditionIsRef[c]));
                }

                drafts.Add(new PolicyRuleDraft(
                    rulePriority is not null && rulePriority.Length > r ? rulePriority[r] : r + 1,
                    ParseEffect(ruleEffect is not null && ruleEffect.Length > r ? ruleEffect[r] : "Deny"),
                    conditions));
            }

            await _policies.UpdateDraftRulesAsync(versionId, drafts, ct);
            TempData["veritas.notice"] = $"Saved {drafts.Count} rule(s) to draft v{version.VersionNumber}.";
        }
        catch (InvalidOperationException ex)
        {
            return View(nameof(Edit), new PolicyEditViewModel
            {
                Version = version,
                Resources = await _resources.ListResourcesAsync(null, null, ct),
                Errors = new[] { ex.Message }
            });
        }

        return RedirectToAction(nameof(Edit), new { versionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transition(Guid versionId, string target, Guid policyId, CancellationToken ct)
    {
        if (!Enum.TryParse<PolicyLifecycleStatus>(target, ignoreCase: true, out var targetStatus))
        {
            TempData["veritas.notice"] = $"Unknown lifecycle state '{target}'.";
            return RedirectToAction(nameof(Details), new { id = policyId });
        }

        var result = await _policies.TransitionAsync(versionId, targetStatus, ct);
        TempData["veritas.notice"] = result.Succeeded
            ? $"Version moved to {target}. Published policy cache invalidated."
            : $"Transition refused: {result.Error}";

        return RedirectToAction(nameof(Details), new { id = policyId, versionId });
    }

    public static IReadOnlyList<string> AttributeCatalog => KnownAttributes;

    private static PolicyEffect ParseEffect(string value) =>
        Enum.TryParse<PolicyEffect>(value, ignoreCase: true, out var effect) ? effect : PolicyEffect.Deny;

    private static ConditionOperator ParseOperator(string value) =>
        Enum.TryParse<ConditionOperator>(value, ignoreCase: true, out var op) ? op : ConditionOperator.Equals;
}
