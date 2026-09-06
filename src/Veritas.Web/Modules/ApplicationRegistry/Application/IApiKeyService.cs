using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.ApplicationRegistry.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.ApplicationRegistry.Application;

public sealed record CreatedApiKey(Guid ApiKeyId, string PlaintextSecret, string KeyPrefix);

public interface IApiKeyService
{
    Task<CreatedApiKey> CreateAsync(Guid serviceAccountId, IEnumerable<string> scopes, TimeSpan? ttl, CancellationToken ct = default);
    Task<CreatedApiKey> RotateAsync(Guid apiKeyId, CancellationToken ct = default);
    Task RevokeAsync(Guid apiKeyId, CancellationToken ct = default);
    Task<ApiKey?> AuthenticateAsync(string presentedPlaintextKey, CancellationToken ct = default);
}

public sealed class ApiKeyService : IApiKeyService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public ApiKeyService(VeritasDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<CreatedApiKey> CreateAsync(Guid serviceAccountId, IEnumerable<string> scopes, TimeSpan? ttl, CancellationToken ct = default)
    {
        var (plaintext, prefix, hash) = GenerateKey();

        var key = new ApiKey
        {
            OrganizationId = _tenant.OrganizationId,
            ServiceAccountId = serviceAccountId,
            KeyPrefix = prefix,
            KeyHash = hash,
            ScopesCsv = string.Join(',', scopes),
            ExpiresAtUtc = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : null
        };

        _db.Set<ApiKey>().Add(key);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("API_KEY_CREATED", serviceAccountId.ToString(), null, prefix, null, key.Id.ToString(), ct);

        // The only point in the key's lifetime where the plaintext secret exists outside this method's stack.
        return new CreatedApiKey(key.Id, plaintext, prefix);
    }

    public async Task<CreatedApiKey> RotateAsync(Guid apiKeyId, CancellationToken ct = default)
    {
        var existing = await _db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == apiKeyId, ct)
            ?? throw new InvalidOperationException("API key not found in this tenant.");

        existing.Revoked = true;
        var created = await CreateAsync(existing.ServiceAccountId, existing.ScopesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries), null, ct);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("API_KEY_ROTATED", existing.ServiceAccountId.ToString(), existing.KeyPrefix, created.KeyPrefix, null, existing.Id.ToString(), ct);
        return created;
    }

    public async Task RevokeAsync(Guid apiKeyId, CancellationToken ct = default)
    {
        var key = await _db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == apiKeyId, ct)
            ?? throw new InvalidOperationException("API key not found in this tenant.");
        key.Revoked = true;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("API_KEY_REVOKED", key.ServiceAccountId.ToString(), null, null, null, key.Id.ToString(), ct);
    }

    public async Task<ApiKey?> AuthenticateAsync(string presentedPlaintextKey, CancellationToken ct = default)
    {
        var hash = Hash(presentedPlaintextKey);
        var key = await _db.Set<ApiKey>()
            .IgnoreQueryFilters() // API key auth must work before tenant is known
            .FirstOrDefaultAsync(k => k.KeyHash == hash && !k.Revoked, ct);

        if (key is null) return null;
        if (key.ExpiresAtUtc is { } exp && exp <= DateTimeOffset.UtcNow) return null;

        key.LastUsedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return key;
    }

    private static (string Plaintext, string Prefix, string Hash) GenerateKey()
    {
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToBase64String(secretBytes).Replace("+", "").Replace("/", "").Replace("=", "");
        var plaintext = $"vk_{secret}";
        var prefix = plaintext[..Math.Min(12, plaintext.Length)];
        return (plaintext, prefix, Hash(plaintext));
    }

    private static string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext)));
}
