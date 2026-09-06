using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.ApplicationRegistry.Domain;

public class ServiceAccount : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Name { get; set; } = default!;
    public string Owner { get; set; } = default!;
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public List<ApiKey> ApiKeys { get; set; } = new();
}

/// <summary>
/// Only ever stores a salted hash (KeyHash) — the plaintext secret is
/// generated once at creation, returned to the caller exactly once, and
/// never persisted anywhere (spec section 28). Authentication re-hashes the
/// presented key and compares hashes.
/// </summary>
public class ApiKey : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ServiceAccountId { get; set; }
    public string KeyPrefix { get; set; } = default!; // shown in UI for identification, e.g. "vk_live_ab12"
    public string KeyHash { get; set; } = default!;    // SHA-256 of the full secret, never the secret itself
    public string ScopesCsv { get; set; } = string.Empty;
    public bool Revoked { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
}
