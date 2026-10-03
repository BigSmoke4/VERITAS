using Microsoft.EntityFrameworkCore;
using Veritas.Web.Shared.Application.AccessGrants;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.RoleManagement.Application;

/// <summary>How a permission was held: through a role, or through a JIT grant.</summary>
public enum PermissionSource { None, Role, TemporaryGrant }

public sealed record PermissionCheckResult(
    bool Satisfied,
    PermissionSource Source,
    IReadOnlyList<string> HeldVia,
    DateTimeOffset? ExpiresAtUtc,
    string Explanation);

/// <summary>The full effective permission set for a subject, resolved from real rows.</summary>
public sealed class EffectivePermissionSet
{
    public required Guid UserId { get; init; }
    public required IReadOnlyList<string> RoleNames { get; init; }
    public required IReadOnlyDictionary<string, string> StaticPermissionToRole { get; init; }
    public required IReadOnlyList<ActiveGrantView> TemporaryGrants { get; init; }

    public IReadOnlySet<string> AllPermissionKeys =>
        StaticPermissionToRole.Keys.Concat(TemporaryGrants.Select(g => g.PermissionKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public interface IRbacEvaluator
{
    Task<EffectivePermissionSet> ResolveAsync(Guid userId, CancellationToken ct = default);
    Task<PermissionCheckResult> CheckAsync(Guid userId, string permissionKey, CancellationToken ct = default);
}

/// <summary>
/// Role-Based Access Control resolution. Two sources count:
/// (1) unexpired UserRole -> RolePermission -> Permission edges, and
/// (2) live temporary grants (via the PrivilegedAccess module's read-only
/// projection, so this module never touches that module's entities).
/// Expired role assignments are excluded at query time, not filtered in memory.
/// </summary>
public sealed class RbacEvaluator : IRbacEvaluator
{
    private readonly VeritasDbContext _db;
    private readonly ITemporaryGrantReader _grants;

    public RbacEvaluator(VeritasDbContext db, ITemporaryGrantReader grants)
    {
        _db = db;
        _grants = grants;
    }

    public async Task<EffectivePermissionSet> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var roleRows = await _db.UserRoles2
            .AsNoTracking()
            .Where(ur => ur.UserId == userId && (ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > now))
            .SelectMany(ur => ur.Role.RolePermissions
                .Select(rp => new { RoleName = ur.Role.Name, PermissionKey = rp.Permission.Key }))
            .ToListAsync(ct);

        var staticMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in roleRows)
            staticMap[row.PermissionKey] = row.RoleName;

        var temporary = await _grants.GetActiveGrantsForUserAsync(userId, ct);

        return new EffectivePermissionSet
        {
            UserId = userId,
            RoleNames = roleRows.Select(r => r.RoleName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StaticPermissionToRole = staticMap,
            TemporaryGrants = temporary
        };
    }

    public async Task<PermissionCheckResult> CheckAsync(Guid userId, string permissionKey, CancellationToken ct = default)
    {
        var effective = await ResolveAsync(userId, ct);

        if (effective.StaticPermissionToRole.TryGetValue(permissionKey, out var roleName))
        {
            return new PermissionCheckResult(
                Satisfied: true,
                Source: PermissionSource.Role,
                HeldVia: new[] { roleName },
                ExpiresAtUtc: null,
                Explanation: $"Held through role '{roleName}'.");
        }

        var grant = effective.TemporaryGrants
            .FirstOrDefault(g => string.Equals(g.PermissionKey, permissionKey, StringComparison.OrdinalIgnoreCase));

        if (grant is not null)
        {
            return new PermissionCheckResult(
                Satisfied: true,
                Source: PermissionSource.TemporaryGrant,
                HeldVia: new[] { $"temporary grant {grant.GrantId:N}" },
                ExpiresAtUtc: grant.ExpiresAtUtc,
                Explanation: $"Held through temporary grant {grant.GrantId:N} (\"{grant.Reason}\"), valid until {grant.ExpiresAtUtc:O}.");
        }

        return new PermissionCheckResult(
            Satisfied: false,
            Source: PermissionSource.None,
            HeldVia: Array.Empty<string>(),
            ExpiresAtUtc: null,
            Explanation: $"No unexpired role grant and no active temporary grant carries '{permissionKey}'.");
    }
}
