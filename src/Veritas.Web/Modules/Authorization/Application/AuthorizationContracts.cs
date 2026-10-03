namespace Veritas.Web.Modules.Authorization.Application;

/// <summary>The full set of attributes a policy condition can reference. Populated from real DB state, never guessed.</summary>
public sealed class AttributeBag
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string? value) => _values[key] = value ?? string.Empty;
    public string Get(string key) => _values.TryGetValue(key, out var v) ? v : string.Empty;
    public bool Has(string key) => _values.ContainsKey(key);
    public IReadOnlyDictionary<string, string> AsReadOnly() => _values;
}

public sealed class AuthorizationRequest
{
    public required string SubjectUserId { get; init; }
    public required string ResourceId { get; init; }
    public required string Action { get; init; }
    public string Environment { get; init; } = "production";
    public string? Ip { get; init; }
    public string? DeviceTrust { get; init; }
    public string? AuthenticationStrength { get; init; }
    public string IdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");
}

public enum AuthorizationDecisionResult
{
    Allow,
    Deny,
    RequireApproval,
    RequireMfa
}

public sealed record MatchedRuleExplanation(
    Guid PolicyId,
    Guid PolicyVersionId,
    int PolicyVersionNumber,
    Guid RuleId,
    int RulePriority,
    string Effect,
    IReadOnlyList<string> ConditionsEvaluated);

/// <summary>
/// One line of the decision explanation (spec section 15). Every gate the
/// request passed through produces exactly one check, whether it passed or
/// failed, so a DENY reads as a list of what was verified and what was not —
/// not a single opaque "denied".
/// </summary>
public sealed record DecisionCheck(
    string Code,
    string Description,
    bool Passed,
    string? Detail = null);

public sealed class AuthorizationDecisionOutcome
{
    public Guid DecisionId { get; } = Guid.NewGuid();
    public required AuthorizationDecisionResult Result { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public IReadOnlyList<DecisionCheck> Checks { get; init; } = Array.Empty<DecisionCheck>();
    public MatchedRuleExplanation? MatchedRule { get; init; }
    public string? PolicyName { get; init; }
    public int? RiskScore { get; init; }
    public string? RiskLevel { get; init; }
    public string? RequiredPermissionKey { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Human-facing identifier used in the API response and the audit trail.</summary>
    public string PublicDecisionId => $"DEC-{DecisionId:N}";
}

/// <summary>
/// Pure, side-effect-free evaluation of a set of published policy rules against
/// an attribute bag. No DB/Redis access happens inside the evaluator itself —
/// callers (IAuthorizationService) are responsible for building the AttributeBag
/// from real data, which keeps this class trivially unit-testable and reusable
/// by both the live authorize endpoint and the Policy Simulator.
/// </summary>
public interface IPolicyEvaluationEngine
{
    AuthorizationDecisionOutcome Evaluate(
        IReadOnlyList<PolicyManagement.Domain.PolicyVersion> candidatePolicyVersions,
        AttributeBag attributes);
}
