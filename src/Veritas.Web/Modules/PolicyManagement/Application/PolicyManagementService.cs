using Microsoft.EntityFrameworkCore;
using Veritas.Web.Infrastructure.Caching;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.PolicyManagement.Application;

public sealed record PolicyConditionDraft(string Attribute, ConditionOperator Operator, string Value, bool IsValueAttributeRef);
public sealed record PolicyRuleDraft(int Priority, PolicyEffect Effect, IReadOnlyList<PolicyConditionDraft> Conditions);

public sealed record PolicySummary(
    Guid Id, string Name, string Owner, int VersionCount, PolicyLifecycleStatus LatestStatus,
    int? PublishedVersionNumber, DateTimeOffset? LastModifiedUtc, DateTimeOffset? LastPublishedUtc);

public sealed record PolicyVersionDetail(
    Guid Id, Guid PolicyId, string PolicyName, int VersionNumber, PolicyLifecycleStatus Status,
    DateTimeOffset? PublishedAtUtc, IReadOnlyList<PolicyRule> Rules);

public sealed record PolicyTransitionResult(bool Succeeded, PolicyLifecycleStatus Status, string? Error);

public interface IPolicyManagementService
{
    Task<IReadOnlyList<PolicySummary>> ListAsync(CancellationToken ct = default);
    Task<Policy?> GetPolicyAsync(Guid policyId, CancellationToken ct = default);
    Task<PolicyVersionDetail?> GetVersionAsync(Guid versionId, CancellationToken ct = default);
    Task<Policy> CreatePolicyAsync(string name, string owner, CancellationToken ct = default);

    /// <summary>Creates the next draft version of a policy. Published versions are immutable.</summary>
    Task<PolicyVersion> CreateDraftVersionAsync(Guid policyId, IReadOnlyList<PolicyRuleDraft> rules, CancellationToken ct = default);

    /// <summary>Replaces the rules of a version that is not yet Published.</summary>
    Task UpdateDraftRulesAsync(Guid versionId, IReadOnlyList<PolicyRuleDraft> rules, CancellationToken ct = default);

    Task<PolicyTransitionResult> TransitionAsync(Guid versionId, PolicyLifecycleStatus target, CancellationToken ct = default);
}

/// <summary>
/// Policy authoring and lifecycle. Enforces the two properties the whole
/// platform depends on:
///  * a Published version is immutable — edits must go through a new version, and
///  * publishing atomically deprecates the previously published version of the
///    same policy and bumps the Redis cache generation, so evaluators never see
///    two "current" versions at once (ADR-004).
/// </summary>
public sealed class PolicyManagementService : IPolicyManagementService
{
    private static readonly Dictionary<PolicyLifecycleStatus, PolicyLifecycleStatus[]> Allowed = new()
    {
        [PolicyLifecycleStatus.Draft] = new[] { PolicyLifecycleStatus.Review, PolicyLifecycleStatus.Deprecated },
        [PolicyLifecycleStatus.Review] = new[] { PolicyLifecycleStatus.Approved, PolicyLifecycleStatus.Draft, PolicyLifecycleStatus.Deprecated },
        [PolicyLifecycleStatus.Approved] = new[] { PolicyLifecycleStatus.Published, PolicyLifecycleStatus.Review, PolicyLifecycleStatus.Deprecated },
        [PolicyLifecycleStatus.Published] = new[] { PolicyLifecycleStatus.Deprecated },
        [PolicyLifecycleStatus.Deprecated] = Array.Empty<PolicyLifecycleStatus>()
    };

    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;
    private readonly IPolicyCache _policyCache;

    public PolicyManagementService(VeritasDbContext db, ITenantContext tenant, IAuditService audit, IPolicyCache policyCache)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
        _policyCache = policyCache;
    }

    public async Task<IReadOnlyList<PolicySummary>> ListAsync(CancellationToken ct = default)
    {
        var policies = await _db.Policies.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var versions = await _db.PolicyVersions.AsNoTracking()
            .Select(v => new { v.PolicyId, v.VersionNumber, v.Status, v.PublishedAtUtc, v.UpdatedAtUtc })
            .ToListAsync(ct);

        return policies.Select(p =>
        {
            var own = versions.Where(v => v.PolicyId == p.Id).OrderByDescending(v => v.VersionNumber).ToList();
            var published = own.FirstOrDefault(v => v.Status == PolicyLifecycleStatus.Published);
            return new PolicySummary(
                p.Id, p.Name, p.Owner, own.Count,
                own.FirstOrDefault()?.Status ?? PolicyLifecycleStatus.Draft,
                published?.VersionNumber,
                own.Select(v => v.UpdatedAtUtc ?? DateTimeOffset.MinValue).DefaultIfEmpty(p.CreatedAtUtc).Max(),
                published?.PublishedAtUtc);
        }).ToList();
    }

    public async Task<Policy?> GetPolicyAsync(Guid policyId, CancellationToken ct = default) =>
        await _db.Policies.AsNoTracking()
            .Include(p => p.Versions).ThenInclude(v => v.Rules).ThenInclude(r => r.Conditions)
            .FirstOrDefaultAsync(p => p.Id == policyId, ct);

    public async Task<PolicyVersionDetail?> GetVersionAsync(Guid versionId, CancellationToken ct = default)
    {
        var version = await _db.PolicyVersions.AsNoTracking()
            .Include(v => v.Policy)
            .Include(v => v.Rules).ThenInclude(r => r.Conditions)
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);

        return version is null ? null : new PolicyVersionDetail(
            version.Id, version.PolicyId, version.Policy?.Name ?? "(unknown)", version.VersionNumber,
            version.Status, version.PublishedAtUtc, version.Rules.OrderBy(r => r.Priority).ToList());
    }

    public async Task<Policy> CreatePolicyAsync(string name, string owner, CancellationToken ct = default)
    {
        var policy = new Policy { OrganizationId = _tenant.OrganizationId, Name = name.Trim(), Owner = owner.Trim() };
        _db.Policies.Add(policy);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("POLICY_CREATED", policy.Id.ToString(), null, policy.Name, null, policy.Id.ToString(), ct);
        return policy;
    }

    public async Task<PolicyVersion> CreateDraftVersionAsync(Guid policyId, IReadOnlyList<PolicyRuleDraft> rules, CancellationToken ct = default)
    {
        var policy = await _db.Policies.FirstOrDefaultAsync(p => p.Id == policyId, ct)
            ?? throw new InvalidOperationException("Policy not found in this tenant.");

        var highestExisting = await _db.PolicyVersions
            .Where(v => v.PolicyId == policyId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(ct);
        var nextNumber = (highestExisting ?? 0) + 1;

        var version = new PolicyVersion
        {
            OrganizationId = _tenant.OrganizationId,
            PolicyId = policyId,
            VersionNumber = nextNumber,
            Status = PolicyLifecycleStatus.Draft
        };
        ApplyRules(version, rules);

        _db.PolicyVersions.Add(version);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("POLICY_VERSION_DRAFTED", policy.Id.ToString(), null, $"v{nextNumber}", null, version.Id.ToString(), ct);
        return version;
    }

    public async Task UpdateDraftRulesAsync(Guid versionId, IReadOnlyList<PolicyRuleDraft> rules, CancellationToken ct = default)
    {
        var version = await _db.PolicyVersions
            .Include(v => v.Rules).ThenInclude(r => r.Conditions)
            .FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new InvalidOperationException("Policy version not found in this tenant.");

        if (version.Status is PolicyLifecycleStatus.Published or PolicyLifecycleStatus.Deprecated)
            throw new InvalidOperationException(
                $"Version {version.VersionNumber} is {version.Status} and immutable. Create a new draft version instead.");

        _db.PolicyConditions.RemoveRange(version.Rules.SelectMany(r => r.Conditions));
        _db.PolicyRules.RemoveRange(version.Rules);
        version.Rules.Clear();
        ApplyRules(version, rules);

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("POLICY_MODIFIED", version.PolicyId.ToString(), null,
            $"v{version.VersionNumber}: {rules.Count} rule(s)", null, version.Id.ToString(), ct);
    }

    public async Task<PolicyTransitionResult> TransitionAsync(Guid versionId, PolicyLifecycleStatus target, CancellationToken ct = default)
    {
        var version = await _db.PolicyVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new InvalidOperationException("Policy version not found in this tenant.");

        if (!Allowed[version.Status].Contains(target))
            return new PolicyTransitionResult(false, version.Status,
                $"Transition {version.Status} -> {target} is not permitted.");

        if (target is PolicyLifecycleStatus.Published && !version.Rules.Any())
            return new PolicyTransitionResult(false, version.Status, "Cannot publish a version with no rules.");

        var previous = version.Status;
        version.Status = target;

        if (target == PolicyLifecycleStatus.Published)
        {
            // Exactly one Published version per policy: retire the incumbent in
            // the same transaction so there is no window with two live versions.
            var incumbent = await _db.PolicyVersions
                .Where(v => v.PolicyId == version.PolicyId && v.Id != version.Id && v.Status == PolicyLifecycleStatus.Published)
                .ToListAsync(ct);

            foreach (var old in incumbent)
                old.Status = PolicyLifecycleStatus.Deprecated;

            version.PublishedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Bumps the Redis cache generation for this tenant (ADR-003).
            await _policyCache.InvalidateAsync(_tenant.OrganizationId, ct);
        }
        else
        {
            await _db.SaveChangesAsync(ct);
        }

        await _audit.RecordAsync(
            action: target == PolicyLifecycleStatus.Published ? "POLICY_PUBLISHED" : $"POLICY_{target.ToString().ToUpperInvariant()}",
            resourceId: version.PolicyId.ToString(),
            previousValue: previous.ToString(),
            newValue: target.ToString(),
            decisionId: null,
            correlationId: version.Id.ToString(),
            ct: ct);

        return new PolicyTransitionResult(true, target, null);
    }

    private static void ApplyRules(PolicyVersion version, IReadOnlyList<PolicyRuleDraft> rules)
    {
        foreach (var draft in rules)
        {
            var rule = new PolicyRule
            {
                PolicyVersionId = version.Id,
                Priority = draft.Priority,
                Effect = draft.Effect
            };

            foreach (var c in draft.Conditions)
            {
                rule.Conditions.Add(new PolicyCondition
                {
                    PolicyRuleId = rule.Id,
                    Attribute = c.Attribute.Trim(),
                    Operator = c.Operator,
                    Value = c.Value.Trim(),
                    IsValueAttributeRef = c.IsValueAttributeRef
                });
            }

            version.Rules.Add(rule);
        }
    }
}
