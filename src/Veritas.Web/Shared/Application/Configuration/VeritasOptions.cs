using System.ComponentModel.DataAnnotations;

namespace Veritas.Web.Shared.Application.Configuration;

/// <summary>
/// Strongly-typed configuration (spec section 67). Every secret arrives through
/// <c>ConnectionStrings:*</c> / environment variables / a secret manager — never
/// from a committed literal. The option types below are bound in Program.cs via
/// <c>builder.Services.AddOptions&lt;T&gt;().BindConfiguration("Veritas:X").ValidateDataAnnotations()</c>.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Veritas:Database";

    /// <summary>Seconds an Npgsql command may run before it is cancelled.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum pooled connections per process. Sized against Postgres max_connections.</summary>
    [Range(1, 512)]
    public int MaxPoolSize { get; set; } = 64;

    /// <summary>Apply pending EF migrations on startup. Keep false where schema changes are a deployment step.</summary>
    public bool AutoMigrate { get; set; }
}

public sealed class RedisOptions
{
    public const string SectionName = "Veritas:Redis";

    /// <summary>
    /// When true, VERITAS runs (with degraded latency) if Redis is unreachable.
    /// PostgreSQL remains the source of truth — see ADR-003.
    /// </summary>
    public bool FailOpen { get; set; } = true;

    [Range(100, 60000)]
    public int ConnectTimeoutMs { get; set; } = 2000;

    [Range(1, 3600)]
    public int PolicyCacheTtlSeconds { get; set; } = 300;

    [Range(1, 86400)]
    public int IdempotencyTtlSeconds { get; set; } = 600;
}

public sealed class SecurityOptions
{
    public const string SectionName = "Veritas:Security";

    /// <summary>Risk score at/above which a policy ALLOW is escalated to REQUIRE_APPROVAL.</summary>
    [Range(1, 100)]
    public int RiskApprovalThreshold { get; set; } = 60;

    /// <summary>Risk score at/above which a policy ALLOW is overridden to DENY.</summary>
    [Range(1, 100)]
    public int RiskDenyThreshold { get; set; } = 80;

    /// <summary>Risk score at/above which additional verification is recommended but ALLOW is retained.</summary>
    [Range(1, 100)]
    public int RiskVerificationThreshold { get; set; } = 30;

    /// <summary>Maximum duration a temporary (JIT) grant may be issued for.</summary>
    public TimeSpan MaxTemporaryGrantDuration { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Maximum duration a privileged session may run.</summary>
    public TimeSpan MaxPrivilegedSessionDuration { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Emit the Strict-Transport-Security header (only meaningful behind TLS).</summary>
    public bool EnableHsts { get; set; } = true;

    public int HstsMaxAgeDays { get; set; } = 365;
}

public sealed class VeritasIdentityOptions
{
    public const string SectionName = "Veritas:Identity";

    [Range(8, 128)]
    public int PasswordMinLength { get; set; } = 12;

    public bool RequireNonAlphanumeric { get; set; } = true;
    public bool RequireUppercase { get; set; } = true;
    public bool RequireLowercase { get; set; } = true;
    public bool RequireDigit { get; set; } = true;
    public bool RequireConfirmedEmail { get; set; } = true;

    [Range(1, 20)]
    public int MaxFailedAccessAttempts { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>How often the cookie principal is re-validated against the live user row,
    /// so a mid-session Suspend/Revoke takes effect without waiting for re-login.</summary>
    public TimeSpan SecurityStampValidationInterval { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class RateLimitOptions
{
    public const string SectionName = "Veritas:RateLimit";

    public int AuthorizationPermitsPerSecond { get; set; } = 50;
    public int AuthorizationQueueLimit { get; set; } = 20;
    public int LoginPermitsPerMinute { get; set; } = 10;
    public int AdminApiPermitsPerSecond { get; set; } = 20;
    public int AccessRequestPermitsPerSecond { get; set; } = 20;
    public int AuditPermitsPerSecond { get; set; } = 30;
    public int ApiKeyAuthPermitsPerSecond { get; set; } = 100;
}

public sealed class AuditOptions
{
    public const string SectionName = "Veritas:Audit";

    /// <summary>Days of audit history retained before CleanupWorker purges it (0 = never).</summary>
    [Range(0, 3650)]
    public int RetentionDays { get; set; } = 400;

    /// <summary>Hard ceiling on the page size any audit query may request.</summary>
    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 100;
}

public sealed class NotificationOptions
{
    public const string SectionName = "Veritas:Notification";

    [Range(1, 20)]
    public int MaxDeliveryAttempts { get; set; } = 5;

    public TimeSpan WorkerPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Outbound webhook timeout; short so a dead endpoint cannot stall the worker.</summary>
    public TimeSpan WebhookTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int OutboxBatchSize { get; set; } = 50;
}

public sealed class WorkerOptions
{
    public const string SectionName = "Veritas:Workers";

    public TimeSpan TemporaryAccessPollInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan SecurityDetectionPollInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan AccessReviewPollInterval { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan CleanupPollInterval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan RiskEvaluationPollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Rows claimed per worker tick. Bounded so one huge backlog cannot turn a
    /// single tick into an unbounded transaction.
    /// </summary>
    [Range(1, 5000)]
    public int BatchSize { get; set; } = 250;
}
