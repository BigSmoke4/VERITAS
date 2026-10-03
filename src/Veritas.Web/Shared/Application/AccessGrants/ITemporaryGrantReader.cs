namespace Veritas.Web.Shared.Application.AccessGrants;

/// <summary>
/// Read-only projection of a temporary (JIT) grant, exposed by the
/// PrivilegedAccess module through <c>Shared</c> so other modules never touch
/// its entities or DbContext directly (spec section 4 — module isolation).
/// </summary>
public sealed record ActiveGrantView(
    Guid GrantId,
    Guid UserId,
    Guid ResourceId,
    string PermissionKey,
    DateTimeOffset StartAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool Revoked,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedReason,
    string Reason);

public interface ITemporaryGrantReader
{
    /// <summary>Every grant for a user, active or not, most recent first.</summary>
    Task<IReadOnlyList<ActiveGrantView>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Most recent grant matching the exact (user, resource, permission) triple — used to explain "why denied".</summary>
    Task<ActiveGrantView?> GetMostRecentGrantAsync(Guid userId, Guid resourceId, string permissionKey, CancellationToken ct = default);

    /// <summary>Currently effective grants for a user, i.e. not revoked and not yet expired.</summary>
    Task<IReadOnlyList<ActiveGrantView>> GetActiveGrantsForUserAsync(Guid userId, CancellationToken ct = default);
}
