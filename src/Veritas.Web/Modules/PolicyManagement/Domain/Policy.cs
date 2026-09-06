using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.PolicyManagement.Domain;

public class Policy : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = default!;
    public string Owner { get; set; } = default!;

    public List<PolicyVersion> Versions { get; set; } = new();
}

public enum PolicyLifecycleStatus
{
    Draft,
    Review,
    Approved,
    Published,
    Deprecated
}

/// <summary>
/// Immutable once Published. Every AuthorizationDecision records the exact
/// (PolicyId, PolicyVersionId) it was evaluated against so a decision made
/// months ago can be reproduced byte-for-byte later.
/// </summary>
public class PolicyVersion : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid PolicyId { get; set; }
    public Policy? Policy { get; set; }
    public int VersionNumber { get; set; }
    public PolicyLifecycleStatus Status { get; set; } = PolicyLifecycleStatus.Draft;
    public DateTimeOffset? PublishedAtUtc { get; set; }

    public List<PolicyRule> Rules { get; set; } = new();
}

/// <summary>Rules are evaluated in Priority order; first match wins.</summary>
public class PolicyRule : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PolicyVersionId { get; set; }
    public int Priority { get; set; }
    public PolicyEffect Effect { get; set; }

    public List<PolicyCondition> Conditions { get; set; } = new();
}

public enum PolicyEffect
{
    Allow,
    Deny,
    RequireApproval,
    RequireMfa
}

/// <summary>
/// A single structured attribute comparison, e.g.
/// Attribute="user.department" Operator=Equals Value="resource.department" (IsValueAttributeRef=true).
/// This is deliberately data, never executable code — the evaluator interprets
/// it, nothing is ever eval()'d.
/// </summary>
public class PolicyCondition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PolicyRuleId { get; set; }
    public string Attribute { get; set; } = default!;
    public ConditionOperator Operator { get; set; }
    public string Value { get; set; } = default!;

    /// <summary>When true, Value is itself an attribute path (e.g. "resource.department") rather than a literal.</summary>
    public bool IsValueAttributeRef { get; set; }
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    LessThanOrEqual,
    GreaterThanOrEqual,
    In
}
