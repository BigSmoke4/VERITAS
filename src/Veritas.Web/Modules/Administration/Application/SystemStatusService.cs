using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Administration.Application;

public sealed record ComponentStatus(string Name, string State, string Detail);

public sealed record SystemStatus(
    IReadOnlyList<ComponentStatus> Components,
    IReadOnlyList<(string Name, string Value)> EffectiveSettings,
    int OpenSecurityEvents, int PendingNotifications, int ActiveTemporaryGrants);

public interface ISystemStatusService
{
    Task<SystemStatus> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Operator-facing view of the platform's own health. Each component state is
/// probed live (a real Postgres round-trip, a real Redis PING) rather than
/// reported from configuration — so "HEALTHY" here means it was just verified.
/// </summary>
public sealed class SystemStatusService : ISystemStatusService
{
    private readonly VeritasDbContext _db;
    private readonly IServiceProvider _services;
    private readonly SecurityOptions _security;
    private readonly RateLimitOptions _rateLimit;
    private readonly AuditOptions _audit;
    private readonly RedisOptions _redis;

    public SystemStatusService(
        VeritasDbContext db,
        IServiceProvider services,
        IOptions<SecurityOptions> security,
        IOptions<RateLimitOptions> rateLimit,
        IOptions<AuditOptions> audit,
        IOptions<RedisOptions> redis)
    {
        _db = db;
        _services = services;
        _security = security.Value;
        _rateLimit = rateLimit.Value;
        _audit = audit.Value;
        _redis = redis.Value;
    }

    public async Task<SystemStatus> GetAsync(CancellationToken ct = default)
    {
        var components = new List<ComponentStatus>();

        try
        {
            var canConnect = await _db.Database.CanConnectAsync(ct);
            components.Add(new ComponentStatus("PostgreSQL", canConnect ? "HEALTHY" : "DEGRADED",
                canConnect ? _db.Database.GetDbConnection().ConnectionString is { } cs ? MaskSecret(cs) : "connected" : "CanConnectAsync returned false"));
        }
        catch (Exception ex)
        {
            components.Add(new ComponentStatus("PostgreSQL", "UNHEALTHY", ex.GetType().Name));
        }

        var multiplexer = _services.GetService(typeof(IConnectionMultiplexer)) as IConnectionMultiplexer;
        if (multiplexer is null)
        {
            components.Add(new ComponentStatus("Redis", "NOT CONFIGURED", "Caching, idempotency and rate limiting fail open to PostgreSQL."));
        }
        else
        {
            try
            {
                var latency = await multiplexer.GetDatabase().PingAsync();
                components.Add(new ComponentStatus("Redis", "HEALTHY", $"PING {latency.TotalMilliseconds:0.##} ms"));
            }
            catch (Exception ex)
            {
                components.Add(new ComponentStatus("Redis", "DEGRADED",
                    $"{ex.GetType().Name} — {_redis.FailOpen} fail-open is {(_redis.FailOpen ? "enabled" : "disabled")}"));
            }
        }

        var openEvents = await _db.SecurityEvents.CountAsync(e => e.Status == Audit.Domain.SecurityEventStatus.Open, ct);
        var pendingNotifications = await _db.Notifications.CountAsync(n =>
            n.Status == Notification.Domain.NotificationStatus.Pending, ct);
        var activeGrants = await _db.TemporaryGrants.CountAsync(g =>
            !g.Revoked && g.ExpiresAtUtc > DateTimeOffset.UtcNow, ct);

        var settings = new List<(string, string)>
        {
            ("security.riskVerificationThreshold", _security.RiskVerificationThreshold.ToString()),
            ("security.riskApprovalThreshold", _security.RiskApprovalThreshold.ToString()),
            ("security.riskDenyThreshold", _security.RiskDenyThreshold.ToString()),
            ("security.maxTemporaryGrantDuration", _security.MaxTemporaryGrantDuration.ToString()),
            ("rateLimit.authorizationPerSecond", _rateLimit.AuthorizationPermitsPerSecond.ToString()),
            ("rateLimit.loginPerMinute", _rateLimit.LoginPermitsPerMinute.ToString()),
            ("audit.retentionDays", _audit.RetentionDays.ToString()),
            ("audit.maxPageSize", _audit.MaxPageSize.ToString()),
            ("redis.policyCacheTtlSeconds", _redis.PolicyCacheTtlSeconds.ToString()),
            ("redis.failOpen", _redis.FailOpen.ToString())
        };

        return new SystemStatus(components, settings, openEvents, pendingNotifications, activeGrants);
    }

    /// <summary>Never render a connection string containing a password into the UI.</summary>
    private static string MaskSecret(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(';', parts.Select(p =>
            p.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase)
                ? "Password=***"
                : p));
    }
}
