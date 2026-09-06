using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Observability;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Infrastructure.Caching;

public interface IPolicyCache
{
    Task<IReadOnlyList<PolicyVersion>> GetPublishedVersionsAsync(Guid organizationId, CancellationToken ct = default);
    Task InvalidateAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// Cache-aside over the Published PolicyVersion set, keyed per-tenant and
/// versioned by a monotonically increasing generation counter stored in
/// Redis. Publishing a new policy version calls InvalidateAsync, which bumps
/// the generation — old cache entries are simply never looked up again
/// rather than requiring a scan-and-delete. If Redis is down, every call
/// falls back to a direct PostgreSQL read: correctness never depends on
/// Redis being up (ADR-003) — only latency does.
/// </summary>
public sealed class RedisPolicyCache : IPolicyCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly IConnectionMultiplexer? _redis;
    private readonly VeritasDbContext _db;
    private readonly ILogger<RedisPolicyCache> _logger;
    private readonly VeritasMetrics _metrics;

    public RedisPolicyCache(IServiceProvider services, VeritasDbContext db, ILogger<RedisPolicyCache> logger, VeritasMetrics metrics)
    {
        // Redis is optional infrastructure: resolved lazily/defensively so the
        // app still runs (with reduced performance) if it isn't registered.
        _redis = services.GetService(typeof(IConnectionMultiplexer)) as IConnectionMultiplexer;
        _db = db;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<IReadOnlyList<PolicyVersion>> GetPublishedVersionsAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (_redis is null)
            return await LoadFromDatabaseAsync(organizationId, ct);

        try
        {
            var db = _redis.GetDatabase();
            var generation = await db.StringGetAsync(GenerationKey(organizationId));
            var cacheKey = DataKey(organizationId, generation.HasValue ? generation.ToString() : "0");

            var cached = await db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                var deserialized = JsonSerializer.Deserialize<List<PolicyVersion>>(cached!);
                if (deserialized is not null)
                {
                    _metrics.RecordCacheHit();
                    return deserialized;
                }
            }

            _metrics.RecordCacheMiss();
            var fromDb = await LoadFromDatabaseAsync(organizationId, ct);
            await db.StringSetAsync(cacheKey, JsonSerializer.Serialize(fromDb), Ttl);
            return fromDb;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable; falling back to direct PostgreSQL read for policy cache.");
            return await LoadFromDatabaseAsync(organizationId, ct);
        }
    }

    public async Task InvalidateAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (_redis is null) return;
        try
        {
            var db = _redis.GetDatabase();
            await db.StringIncrementAsync(GenerationKey(organizationId));
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable; policy cache invalidation is a no-op (next read hits Postgres anyway once TTL expires).");
        }
    }

    private async Task<List<PolicyVersion>> LoadFromDatabaseAsync(Guid organizationId, CancellationToken ct) =>
        await _db.PolicyVersions
            .IgnoreQueryFilters()
            .Where(pv => pv.OrganizationId == organizationId && pv.Status == PolicyLifecycleStatus.Published)
            .Include(pv => pv.Rules).ThenInclude(r => r.Conditions)
            .AsNoTracking()
            .ToListAsync(ct);

    private static string GenerationKey(Guid orgId) => $"veritas:policy:gen:{orgId}";
    private static string DataKey(Guid orgId, string generation) => $"veritas:policy:data:{orgId}:{generation}";
}
