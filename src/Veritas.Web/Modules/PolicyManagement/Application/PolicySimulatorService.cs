using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.PolicyManagement.Application;

public sealed record SimulatedUserOutcome(Guid UserId, string DisplayName, AuthorizationDecisionResult Before, AuthorizationDecisionResult After);

public sealed record PolicySimulationResult(
    int UsersAffected,
    int AllowedBefore,
    int AllowedAfter,
    int UsersLosingAccess,
    int UsersGainingAccess,
    IReadOnlyList<SimulatedUserOutcome> Details);

public interface IPolicySimulatorService
{
    /// <summary>
    /// Runs the SAME PolicyEvaluationEngine used by the live authorize
    /// endpoint against every real user for a given resource/action, once
    /// with the currently-Published policy set and once with the candidate
    /// (not-yet-published) version substituted in. No results are faked or
    /// interpolated — this is the real evaluator run twice per user.
    /// </summary>
    Task<PolicySimulationResult> SimulateAsync(Guid candidatePolicyVersionId, Guid resourceId, string action, CancellationToken ct = default);
}

public sealed class PolicySimulatorService : IPolicySimulatorService
{
    private readonly VeritasDbContext _db;
    private readonly IPolicyEvaluationEngine _engine;

    public PolicySimulatorService(VeritasDbContext db, IPolicyEvaluationEngine engine)
    {
        _db = db;
        _engine = engine;
    }

    public async Task<PolicySimulationResult> SimulateAsync(Guid candidatePolicyVersionId, Guid resourceId, string action, CancellationToken ct = default)
    {
        var candidateVersion = await _db.PolicyVersions
            .Include(v => v.Rules).ThenInclude(r => r.Conditions)
            .FirstOrDefaultAsync(v => v.Id == candidatePolicyVersionId, ct)
            ?? throw new InvalidOperationException("Candidate policy version not found in this tenant.");

        var resource = await _db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId, ct)
            ?? throw new InvalidOperationException("Resource not found in this tenant.");

        var currentPublished = await _db.PolicyVersions
            .Where(v => v.Status == PolicyLifecycleStatus.Published && v.PolicyId == candidateVersion.PolicyId)
            .Include(v => v.Rules).ThenInclude(r => r.Conditions)
            .ToListAsync(ct);

        var otherPublished = await _db.PolicyVersions
            .Where(v => v.Status == PolicyLifecycleStatus.Published && v.PolicyId != candidateVersion.PolicyId)
            .Include(v => v.Rules).ThenInclude(r => r.Conditions)
            .ToListAsync(ct);

        var users = await _db.Users.ToListAsync(ct);

        var beforeSet = otherPublished.Concat(currentPublished).ToList();
        var afterSet = otherPublished.Concat(new[] { candidateVersion }).ToList();

        var details = new List<SimulatedUserOutcome>();

        foreach (var user in users)
        {
            var attributes = new AttributeBag();
            attributes.Set("user.id", user.Id.ToString());
            attributes.Set("user.status", user.LifecycleState);
            attributes.Set("user.riskLevel", user.RiskLevel);
            attributes.Set("resource.id", resource.Id.ToString());
            attributes.Set("resource.classification", resource.Classification);
            attributes.Set("resource.department", resource.OwnerDepartment);
            attributes.Set("resource.environment", resource.Environment);
            attributes.Set("request.action", action);

            var before = _engine.Evaluate(beforeSet, attributes).Result;
            var after = _engine.Evaluate(afterSet, attributes).Result;

            details.Add(new SimulatedUserOutcome(user.Id, user.DisplayName ?? user.UserName ?? user.Id.ToString(), before, after));
        }

        return new PolicySimulationResult(
            UsersAffected: details.Count(d => d.Before != d.After),
            AllowedBefore: details.Count(d => d.Before == AuthorizationDecisionResult.Allow),
            AllowedAfter: details.Count(d => d.After == AuthorizationDecisionResult.Allow),
            UsersLosingAccess: details.Count(d => d.Before == AuthorizationDecisionResult.Allow && d.After != AuthorizationDecisionResult.Allow),
            UsersGainingAccess: details.Count(d => d.Before != AuthorizationDecisionResult.Allow && d.After == AuthorizationDecisionResult.Allow),
            Details: details);
    }
}
