using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.RoleManagement.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.RoleManagement.Application;

public sealed record RoleSummary(Guid Id, string Name, string Description, bool IsSystemRole, int PermissionCount, int AssignedUserCount);

public sealed record RoleDetail(Guid Id, string Name, string Description, bool IsSystemRole,
    IReadOnlyList<Permission> Permissions, IReadOnlyList<Guid> AssignedUserIds);

public sealed record SoDRuleSummary(Guid Id, string PermissionKeyA, string PermissionKeyB, string Description);

public interface IRoleService
{
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken ct = default);
    Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken ct = default);
    Task<Role> CreateAsync(string name, string description, IReadOnlyList<Guid> permissionIds, CancellationToken ct = default);
    Task SetPermissionsAsync(Guid roleId, IReadOnlyList<Guid> permissionIds, CancellationToken ct = default);
    Task<IReadOnlyList<SoDRuleSummary>> ListSoDRulesAsync(CancellationToken ct = default);
    Task<SoDConflictRule> CreateSoDRuleAsync(string keyA, string keyB, string description, CancellationToken ct = default);
}

public sealed class RoleService : IRoleService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public RoleService(VeritasDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken ct = default)
    {
        var roles = await _db.Roles2.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);

        var permCounts = await _db.RolePermissions.AsNoTracking()
            .GroupBy(rp => rp.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, ct);

        var userCounts = await _db.UserRoles2.AsNoTracking()
            .Where(ur => ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .GroupBy(ur => ur.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, ct);

        return roles.Select(r => new RoleSummary(
            r.Id, r.Name, r.Description, r.IsSystemRole,
            permCounts.TryGetValue(r.Id, out var p) ? p : 0,
            userCounts.TryGetValue(r.Id, out var u) ? u : 0)).ToList();
    }

    public async Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken ct = default)
    {
        var role = await _db.Roles2.AsNoTracking()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null) return null;

        var users = await _db.UserRoles2.AsNoTracking()
            .Where(ur => ur.RoleId == roleId && (ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > DateTimeOffset.UtcNow))
            .Select(ur => ur.UserId).Distinct().ToListAsync(ct);

        return new RoleDetail(role.Id, role.Name, role.Description, role.IsSystemRole,
            role.RolePermissions.Select(rp => rp.Permission).OrderBy(p => p.Key).ToList(), users);
    }

    public async Task<Role> CreateAsync(string name, string description, IReadOnlyList<Guid> permissionIds, CancellationToken ct = default)
    {
        var role = new Role { OrganizationId = _tenant.OrganizationId, Name = name.Trim(), Description = description.Trim() };
        _db.Roles2.Add(role);

        foreach (var pid in permissionIds.Distinct())
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = pid });

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("ROLE_CREATED", role.Id.ToString(), null, role.Name, null, role.Id.ToString(), ct);
        return role;
    }

    public async Task SetPermissionsAsync(Guid roleId, IReadOnlyList<Guid> permissionIds, CancellationToken ct = default)
    {
        var role = await _db.Roles2.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new InvalidOperationException("Role not found in this tenant.");

        var desired = permissionIds.Distinct().ToHashSet();
        var current = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();

        foreach (var remove in current.Except(desired))
            _db.RolePermissions.Remove(role.RolePermissions.First(rp => rp.PermissionId == remove));

        foreach (var add in desired.Except(current))
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = add });

        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("ROLE_MODIFIED", role.Id.ToString(), $"{current.Count} permission(s)",
            $"{desired.Count} permission(s)", null, role.Id.ToString(), ct);
    }

    public async Task<IReadOnlyList<SoDRuleSummary>> ListSoDRulesAsync(CancellationToken ct = default) =>
        await _db.SoDConflictRules.AsNoTracking()
            .OrderBy(r => r.PermissionKeyA)
            .Select(r => new SoDRuleSummary(r.Id, r.PermissionKeyA, r.PermissionKeyB, r.Description))
            .ToListAsync(ct);

    public async Task<SoDConflictRule> CreateSoDRuleAsync(string keyA, string keyB, string description, CancellationToken ct = default)
    {
        var rule = new SoDConflictRule
        {
            OrganizationId = _tenant.OrganizationId,
            PermissionKeyA = keyA.Trim().ToLowerInvariant(),
            PermissionKeyB = keyB.Trim().ToLowerInvariant(),
            Description = description.Trim()
        };
        _db.SoDConflictRules.Add(rule);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("SOD_RULE_CREATED", rule.Id.ToString(), null,
            $"{rule.PermissionKeyA} <> {rule.PermissionKeyB}", null, rule.Id.ToString(), ct);
        return rule;
    }
}
