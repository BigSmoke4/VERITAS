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
    public string Environment { get; set; } = "production";
    public required string Result { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? PolicyVersionId { get; set; }
    public int? RiskScore { get; set; }
    public string? RiskLevel { get; set; }
    public string? RequiredPermissionKey { get; set; }

    /// <summary>JSON array of reason strings, exactly as returned to the caller.</summary>
    public string ReasonsJson { get; set; } = "[]";

    /// <summary>JSON array of DecisionCheck — the pass/fail transcript behind the decision.</summary>
    public string ChecksJson { get; set; } = "[]";

    /// <summary>JSON snapshot of the request context (ip, deviceTrust, authStrength, idempotency key).</summary>
    public string ContextJson { get; set; } = "{}";

    public string? CorrelationId { get; set; }
    public DateTimeOffset EvaluatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

/// <summary>
/// A detection produced by SecurityDetectionWorker from real audit/decision
/// data. Findings are deduplicated by (Detector, SubjectKey, Window) so a
/// repeating pattern does not spam the queue on every poll.
/// </summary>
public class SecurityEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>e.g. REPEATED_DENIALS, RAPID_PRIVILEGE_ESCALATION, DORMANT_PRIVILEGED_ACCOUNT.</summary>
    public required string Detector { get; set; }

    /// <summary>LOW / MEDIUM / HIGH / CRITICAL.</summary>
    public string Severity { get; set; } = "MEDIUM";
    public string Title { get; set; } = default!;
    public string Detail { get; set; } = default!;

    public Guid? SubjectUserId { get; set; }
    public string? ResourceId { get; set; }

    /// <summary>Stable key used to suppress duplicate findings for the same subject+window.</summary>
    public required string DedupeKey { get; set; }

    public string Status { get; set; } = SecurityEventStatus.Open;
    public DateTimeOffset DetectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
}

public static class SecurityEventStatus
{
    public const string Open = "OPEN";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Resolved = "RESOLVED";
}
