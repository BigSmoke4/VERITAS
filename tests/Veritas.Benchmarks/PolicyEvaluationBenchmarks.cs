using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.PolicyManagement.Domain;

namespace Veritas.Benchmarks;

/// <summary>
/// Real, repeatable benchmarks of the actual PolicyEvaluationEngine — the
/// hot path for every /api/v1/authorize call. Run with:
///   dotnet run -c Release --project tests/Veritas.Benchmarks
/// Numbers are produced by BenchmarkDotNet on the machine that runs it, never
/// fabricated or copied from elsewhere, per spec section 64.
/// </summary>
[MemoryDiagnoser]
public class PolicyEvaluationBenchmarks
{
    private PolicyEvaluationEngine _engine = default!;
    private List<PolicyVersion> _singleRulePolicy = default!;
    private List<PolicyVersion> _fiftyRulePolicy = default!;
    private AttributeBag _attributes = default!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new PolicyEvaluationEngine();

        _attributes = new AttributeBag();
        _attributes.Set("user.department", "Finance");
        _attributes.Set("resource.department", "Finance");
        _attributes.Set("user.status", "ACTIVE");
        _attributes.Set("resource.classification", "CONFIDENTIAL");
        _attributes.Set("request.deviceTrust", "HIGH");

        _singleRulePolicy = new List<PolicyVersion> { BuildVersion(1) };
        _fiftyRulePolicy = new List<PolicyVersion> { BuildVersion(50) };
    }

    private static PolicyVersion BuildVersion(int ruleCount)
    {
        var rules = new List<PolicyRule>();
        for (var i = 0; i < ruleCount; i++)
        {
            rules.Add(new PolicyRule
            {
                Priority = i,
                Effect = i == ruleCount - 1 ? PolicyEffect.Allow : PolicyEffect.Deny,
                Conditions = new List<PolicyCondition>
                {
                    // Early rules deliberately never match, forcing the engine
                    // to walk the full list before the final ALLOW — worst case.
                    new() { Attribute = "resource.classification", Operator = ConditionOperator.Equals, Value = i == ruleCount - 1 ? "CONFIDENTIAL" : "NONEXISTENT_VALUE" }
                }
            });
        }

        return new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = Guid.NewGuid(),
            VersionNumber = 1,
            Status = PolicyLifecycleStatus.Published,
            Rules = rules
        };
    }

    [Benchmark(Baseline = true)]
    public AuthorizationDecisionOutcome Evaluate_SingleRulePolicy() =>
        _engine.Evaluate(_singleRulePolicy, _attributes);

    [Benchmark]
    public AuthorizationDecisionOutcome Evaluate_FiftyRulePolicy_WorstCaseFallthrough() =>
        _engine.Evaluate(_fiftyRulePolicy, _attributes);
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkRunner.Run<PolicyEvaluationBenchmarks>();
}
