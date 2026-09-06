using Veritas.Web.Modules.PolicyManagement.Domain;

namespace Veritas.Web.Modules.Authorization.Application;

/// <summary>
/// Deterministic, explainable evaluator. Rules across all candidate published
/// policy versions are flattened and sorted by Priority (ascending = evaluated
/// first). The first rule whose every condition matches wins. If nothing
/// matches, the decision is Deny-by-default (Zero Trust: no implicit allow).
/// </summary>
public sealed class PolicyEvaluationEngine : IPolicyEvaluationEngine
{
    public AuthorizationDecisionOutcome Evaluate(
        IReadOnlyList<PolicyVersion> candidatePolicyVersions,
        AttributeBag attributes)
    {
        var orderedRules = candidatePolicyVersions
            .Where(pv => pv.Status == PolicyLifecycleStatus.Published)
            .SelectMany(pv => pv.Rules.Select(rule => (Version: pv, Rule: rule)))
            .OrderBy(x => x.Rule.Priority)
            .ToList();

        if (orderedRules.Count == 0)
        {
            return new AuthorizationDecisionOutcome
            {
                Result = AuthorizationDecisionResult.Deny,
                Reasons = new[] { "No published policy applies to this resource/action. Zero Trust default is DENY." }
            };
        }

        foreach (var (version, rule) in orderedRules)
        {
            var conditionResults = rule.Conditions
                .Select(c => (Condition: c, Matched: EvaluateCondition(c, attributes)))
                .ToList();

            if (conditionResults.All(r => r.Matched))
            {
                var explanation = new MatchedRuleExplanation(
                    PolicyId: version.PolicyId,
                    PolicyVersionId: version.Id,
                    PolicyVersionNumber: version.VersionNumber,
                    RuleId: rule.Id,
                    RulePriority: rule.Priority,
                    Effect: rule.Effect.ToString(),
                    ConditionsEvaluated: conditionResults
                        .Select(r => $"{r.Condition.Attribute} {r.Condition.Operator} {r.Condition.Value} => {(r.Matched ? "MATCH" : "NO MATCH")}")
                        .ToList());

                var result = rule.Effect switch
                {
                    PolicyEffect.Allow => AuthorizationDecisionResult.Allow,
                    PolicyEffect.Deny => AuthorizationDecisionResult.Deny,
                    PolicyEffect.RequireApproval => AuthorizationDecisionResult.RequireApproval,
                    PolicyEffect.RequireMfa => AuthorizationDecisionResult.RequireMfa,
                    _ => AuthorizationDecisionResult.Deny
                };

                var reasons = new List<string>
                {
                    $"Matched rule {rule.Priority} on policy version {version.VersionNumber} -> {rule.Effect}"
                };
                reasons.AddRange(explanation.ConditionsEvaluated);

                return new AuthorizationDecisionOutcome
                {
                    Result = result,
                    Reasons = reasons,
                    MatchedRule = explanation
                };
            }
        }

        return new AuthorizationDecisionOutcome
        {
            Result = AuthorizationDecisionResult.Deny,
            Reasons = new[] { "No rule's conditions fully matched across any published policy. Zero Trust default is DENY." }
        };
    }

    private static bool EvaluateCondition(PolicyCondition condition, AttributeBag attributes)
    {
        var left = attributes.Get(condition.Attribute);
        var right = condition.IsValueAttributeRef ? attributes.Get(condition.Value) : condition.Value;

        return condition.Operator switch
        {
            ConditionOperator.Equals => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.In => right.Split(',', StringSplitOptions.TrimEntries)
                .Any(v => string.Equals(v, left, StringComparison.OrdinalIgnoreCase)),
            ConditionOperator.LessThanOrEqual => CompareOrdinalRank(left) <= CompareOrdinalRank(right),
            ConditionOperator.GreaterThanOrEqual => CompareOrdinalRank(left) >= CompareOrdinalRank(right),
            _ => false
        };
    }

    /// <summary>
    /// Ordinal ranking for classification/trust-style attributes so LessThanOrEqual
    /// comparisons (e.g. resource.classification &lt;= user.clearance) are meaningful.
    /// Unknown values rank lowest, which fails open toward DENY, never ALLOW.
    /// </summary>
    private static int CompareOrdinalRank(string value) => value.ToUpperInvariant() switch
    {
        "PUBLIC" or "LOW" => 0,
        "INTERNAL" or "MEDIUM" => 1,
        "CONFIDENTIAL" or "HIGH" => 2,
        "HIGHLY_CONFIDENTIAL" or "CRITICAL" => 3,
        _ => -1
    };
}
