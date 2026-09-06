using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.Audit.Domain;

/// <summary>
/// Append-only from the application's perspective: no AuditService method ever
/// updates or deletes a row. DB-level: revoke UPDATE/DELETE grants on this table
/// for the application role in production (see docs/security.md).
/// </summary>
public class AuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public string? ResourceId { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public Guid? DecisionId { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Ip { get; set; }
}

public class AuthorizationDecisionRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string SubjectUserId { get; set; }
    public required string ResourceId { get; set; }
    public required string Action { get; set; }
    public required string Result { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? PolicyVersionId { get; set; }
    public int? RiskScore { get; set; }
    public string ReasonsJson { get; set; } = "[]";
    public DateTimeOffset EvaluatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
