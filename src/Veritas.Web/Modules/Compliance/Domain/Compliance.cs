using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.Compliance.Domain;

/// <summary>
/// A standing control the organisation has committed to (e.g. "no dormant
/// privileged account may stay unreviewed for more than 90 days"). Controls
/// are data, not code: ComplianceService interprets a small closed set of
/// control types, so nothing here is ever eval'd.
/// </summary>
public class CompliancePolicy : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>e.g. DORMANT_PRIVILEGED_ACCOUNT, EXCESSIVE_PRIVILEGES, ORPHANED_ACCOUNT, EXPIRED_TEMPORARY_ACCESS, UNUSED_PERMISSION.</summary>
    public required string ControlType { get; set; }

    public string Title { get; set; } = default!;
    public string Description { get; set; } = string.Empty;

    /// <summary>LOW / MEDIUM / HIGH / CRITICAL — the severity stamped on findings this control produces.</summary>
    public string Severity { get; set; } = "MEDIUM";

    /// <summary>Control-specific numeric parameter (days of dormancy, max roles per user, ...).</summary>
    public int Threshold { get; set; } = 90;

    public bool Enabled { get; set; } = true;
    public string? Framework { get; set; } // e.g. "SOC2 CC6.1", "ISO 27001 A.9.2"
}

public class ComplianceFinding : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid CompliancePolicyId { get; set; }
    public CompliancePolicy? CompliancePolicy { get; set; }

    public required string ControlType { get; set; }
    public string Severity { get; set; } = "MEDIUM";
    public string Title { get; set; } = default!;
    public string Detail { get; set; } = default!;

    /// <summary>The offending subject, when the finding is about a specific user/resource.</summary>
    public Guid? SubjectUserId { get; set; }
    public Guid? SubjectResourceId { get; set; }

    /// <summary>Stable identity for the finding, so re-running a scan updates rather than duplicates.</summary>
    public required string DedupeKey { get; set; }

    public string Status { get; set; } = ComplianceFindingStatus.Open;
    public DateTimeOffset FirstDetectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

public static class ComplianceFindingStatus
{
    public const string Open = "OPEN";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Resolved = "RESOLVED";
}

public static class ControlTypes
{
    public const string DormantPrivilegedAccount = "DORMANT_PRIVILEGED_ACCOUNT";
    public const string InactiveUser = "INACTIVE_USER";
    public const string UnusedPermission = "UNUSED_PERMISSION";
    public const string OrphanedAccount = "ORPHANED_ACCOUNT";
    public const string ExcessivePrivileges = "EXCESSIVE_PRIVILEGES";
    public const string ExpiredTemporaryAccess = "EXPIRED_TEMPORARY_ACCESS";

    public static readonly IReadOnlyList<string> All = new[]
    {
        DormantPrivilegedAccount, InactiveUser, UnusedPermission,
        OrphanedAccount, ExcessivePrivileges, ExpiredTemporaryAccess
    };
}
