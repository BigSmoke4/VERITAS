using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Xunit;

namespace Veritas.Tests;

public class PolicyEvaluatorTests
{
    private static PolicyVersion PublishedVersion(params PolicyRule[] rules) => new()
    {
        Id = Guid.NewGuid(),
        PolicyId = Guid.NewGuid(),
        VersionNumber = 1,
        Status = PolicyLifecycleStatus.Published,
        Rules = rules.ToList()
    };

    [Fact]
    public void NoPublishedPolicies_DeniesByDefault()
    {
        var engine = new PolicyEvaluationEngine();
        var result = engine.Evaluate(Array.Empty<PolicyVersion>(), new AttributeBag());

        Assert.Equal(AuthorizationDecisionResult.Deny, result.Result);
    }

    [Fact]
    public void MatchingAllowRule_ReturnsAllow()
    {
        var rule = new PolicyRule
        {
            Priority = 1,
            Effect = PolicyEffect.Allow,
            Conditions = new List<PolicyCondition>
            {
                new() { Attribute = "user.department", Operator = ConditionOperator.Equals, Value = "resource.department", IsValueAttributeRef = true },
                new() { Attribute = "user.status", Operator = ConditionOperator.Equals, Value = "ACTIVE" }
            }
        };
        var version = PublishedVersion(rule);

        var attributes = new AttributeBag();
        attributes.Set("user.department", "Finance");
        attributes.Set("resource.department", "Finance");
        attributes.Set("user.status", "ACTIVE");

        var engine = new PolicyEvaluationEngine();
        var result = engine.Evaluate(new[] { version }, attributes);

        Assert.Equal(AuthorizationDecisionResult.Allow, result.Result);
        Assert.NotNull(result.MatchedRule);
    }

    [Fact]
    public void DestructiveActionInProduction_RequiresApproval()
    {
        var rule = new PolicyRule
        {
            Priority = 1,
            Effect = PolicyEffect.RequireApproval,
            Conditions = new List<PolicyCondition>
            {
                new() { Attribute = "request.action", Operator = ConditionOperator.Equals, Value = "DELETE" },
                new() { Attribute = "resource.environment", Operator = ConditionOperator.Equals, Value = "production" }
            }
        };
        var version = PublishedVersion(rule);

        var attributes = new AttributeBag();
        attributes.Set("request.action", "DELETE");
        attributes.Set("resource.environment", "production");

        var result = new PolicyEvaluationEngine().Evaluate(new[] { version }, attributes);

        Assert.Equal(AuthorizationDecisionResult.RequireApproval, result.Result);
    }

    [Fact]
    public void HighlyConfidentialResource_WithoutHighDeviceTrust_Denies()
    {
        var rule = new PolicyRule
        {
            Priority = 1,
            Effect = PolicyEffect.Deny,
            Conditions = new List<PolicyCondition>
            {
                new() { Attribute = "resource.classification", Operator = ConditionOperator.Equals, Value = "HIGHLY_CONFIDENTIAL" },
                new() { Attribute = "request.deviceTrust", Operator = ConditionOperator.NotEquals, Value = "HIGH" }
            }
        };
        var version = PublishedVersion(rule);

        var attributes = new AttributeBag();
        attributes.Set("resource.classification", "HIGHLY_CONFIDENTIAL");
        attributes.Set("request.deviceTrust", "MEDIUM");

        var result = new PolicyEvaluationEngine().Evaluate(new[] { version }, attributes);

        Assert.Equal(AuthorizationDecisionResult.Deny, result.Result);
    }

    [Fact]
    public void FirstMatchingRuleByPriority_Wins_EvenIfLaterRuleWouldAlsoMatch()
    {
        var denyFirst = new PolicyRule
        {
            Priority = 1,
            Effect = PolicyEffect.Deny,
            Conditions = new List<PolicyCondition>
            {
                new() { Attribute = "resource.classification", Operator = ConditionOperator.Equals, Value = "CONFIDENTIAL" }
            }
        };
        var allowSecond = new PolicyRule
        {
            Priority = 2,
            Effect = PolicyEffect.Allow,
            Conditions = new List<PolicyCondition>
            {
                new() { Attribute = "resource.classification", Operator = ConditionOperator.Equals, Value = "CONFIDENTIAL" }
            }
        };
        var version = PublishedVersion(denyFirst, allowSecond);

        var attributes = new AttributeBag();
        attributes.Set("resource.classification", "CONFIDENTIAL");

        var result = new PolicyEvaluationEngine().Evaluate(new[] { version }, attributes);

        Assert.Equal(AuthorizationDecisionResult.Deny, result.Result);
        Assert.Equal(1, result.MatchedRule!.RulePriority);
    }

    [Fact]
    public void DraftPolicyVersion_IsIgnored_EvenIfConditionsMatch()
    {
        var rule = new PolicyRule
        {
            Priority = 1,
            Effect = PolicyEffect.Allow,
            Conditions = new List<PolicyCondition>
            {
                new() { Attribute = "user.status", Operator = ConditionOperator.Equals, Value = "ACTIVE" }
            }
        };
        var draftVersion = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = Guid.NewGuid(),
            VersionNumber = 2,
            Status = PolicyLifecycleStatus.Draft,
            Rules = new List<PolicyRule> { rule }
        };

        var attributes = new AttributeBag();
        attributes.Set("user.status", "ACTIVE");

        var result = new PolicyEvaluationEngine().Evaluate(new[] { draftVersion }, attributes);

        Assert.Equal(AuthorizationDecisionResult.Deny, result.Result);
    }

    [Fact]
    public void ClassificationLessThanOrEqualClearance_Allows()
    {
        var rule = new PolicyRule
        {
            Priority = 1,
            Effect = PolicyEffect.Allow,
            Conditions = new List<PolicyCondition>
            {
                new() { Attribute = "resource.classification", Operator = ConditionOperator.LessThanOrEqual, Value = "user.clearance", IsValueAttributeRef = true }
            }
        };
        var version = PublishedVersion(rule);

        var attributes = new AttributeBag();
        attributes.Set("resource.classification", "INTERNAL");
        attributes.Set("user.clearance", "CONFIDENTIAL");

        var result = new PolicyEvaluationEngine().Evaluate(new[] { version }, attributes);

        Assert.Equal(AuthorizationDecisionResult.Allow, result.Result);
    }
}
