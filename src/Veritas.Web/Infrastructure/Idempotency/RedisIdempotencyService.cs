using System.Text.Json;
using StackExchange.Redis;

namespace Veritas.Web.Infrastructure.Idempotency;

public interface IIdempotencyService
{
    /// <summary>
    /// Returns a previously stored response body for this key if one exists
    /// (meaning this exact request was already processed), otherwise null.
    /// </summary>
    Task<string?> TryGetCachedResponseAsync(string idempotencyKey, CancellationToken ct = default);

    Task StoreResponseAsync(string idempotencyKey, string responseBody, TimeSpan ttl, CancellationToken ct = default);
}

/// <summary>
/// Backed by Redis SETNX-style semantics via StringSet with When.NotExists so
/// two concurrent requests carrying the same Idempotency-Key can't both
/// "win" and double-process (e.g. double-grant access, double-rotate a key).
/// If Redis is unavailable, idempotency enforcement fails open (logs and lets
/// the request through) rather than blocking all mutating traffic — see
/// ADR-003 for the reasoning.
/// </summary>
public sealed class RedisIdempotencyService : IIdempotencyService
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<RedisIdempotencyService> _logger;

    public RedisIdempotencyService(IServiceProvider services, ILogger<RedisIdempotencyService> logger)
    {
        _redis = services.GetService(typeof(IConnectionMultiplexer)) as IConnectionMultiplexer;
        _logger = logger;
    }

    public async Task<string?> TryGetCachedResponseAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (_redis is null || string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(Key(idempotencyKey));
            return value.HasValue ? value.ToString() : null;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable; idempotency check fails open.");
            return null;
        }
    }

    public async Task StoreResponseAsync(string idempotencyKey, string responseBody, TimeSpan ttl, CancellationToken ct = default)
    {
        if (_redis is null || string.IsNullOrWhiteSpace(idempotencyKey)) return;
        try
        {
            await _redis.GetDatabase().StringSetAsync(Key(idempotencyKey), responseBody, ttl, When.NotExists);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable; idempotency result was not cached (best-effort only).");
        }
    }

    private static string Key(string idempotencyKey) => $"veritas:idempotency:{idempotencyKey}";
}
