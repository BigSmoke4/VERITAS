using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.RoleManagement.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.PermissionManagement.Application;

public sealed record PermissionSummary(Guid Id, string Key, string Description, string ResourcePrefix, string Action, int RoleCount, int HolderCount);

public interface IPermissionService
{
    Task<IReadOnlyList<PermissionSummary>> ListAsync(string? prefix, CancellationToken ct = default);
    Task<Permission> CreateAsync(string key, string description, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListPrefixesAsync(CancellationToken ct = default);
}

/// <summary>
/// Permissions follow the <c>resource.action</c> convention (spec section 9).
/// The split into prefix/action is derived from the stored key, and holder
/// counts come from real UserRole -> RolePermission edges so "unused
/// permission" is an observable fact rather than a guess.
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public PermissionService(VeritasDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<IReadOnlyList<PermissionSummary>> ListAsync(string? prefix, CancellationToken ct = default)
    {
        var query = _db.Permissions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var p = prefix.Trim() + ".";
            query = query.Where(x => x.Key.StartsWith(p));
        }

        var permissions = await query.OrderBy(x => x.Key).ToListAsync(ct);

        var roleCounts = await _db.RolePermissions.AsNoTracking()
            .GroupBy(rp => rp.PermissionId).Select(g => new { PermissionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PermissionId, x => x.Count, ct);

        var holderCounts = await _db.UserRoles2.AsNoTracking()
            .Where(ur => ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => new { rp.PermissionId, ur.UserId }))
            .Distinct()
            .GroupBy(x => x.PermissionId).Select(g => new { PermissionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PermissionId, x => x.Count, ct);

        return permissions.Select(p =>
        {
            var dot = p.Key.IndexOf('.');
            var res = dot > 0 ? p.Key[..dot] : p.Key;
            var act = dot > 0 ? p.Key[(dot + 1)..] : string.Empty;
            return new PermissionSummary(p.Id, p.Key, p.Description, res, act,
                roleCounts.TryGetValue(p.Id, out var rc) ? rc : 0,
                holderCounts.TryGetValue(p.Id, out var hc) ? hc : 0);
        }).ToList();
    }

    public async Task<Permission> CreateAsync(string key, string description, CancellationToken ct = default)
    {
        var normalized = key.Trim().ToLowerInvariant();
        if (!normalized.Contains('.') || normalized.StartsWith('.') || normalized.EndsWith('.'))
            throw new InvalidOperationException("Permission keys must follow the 'resource.action' convention, e.g. 'payment.read'.");

        var permission = new Permission { OrganizationId = _tenant.OrganizationId, Key = normalized, Description = description.Trim() };
        _db.Permissions.Add(permission);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("PERMISSION_CREATED", permission.Id.ToString(), null, permission.Key, null, permission.Id.ToString(), ct);
        return permission;
    }

    public async Task<IReadOnlyList<string>> ListPrefixesAsync(CancellationToken ct = default)
    {
        var keys = await _db.Permissions.AsNoTracking().Select(p => p.Key).ToListAsync(ct);
        return keys.Where(k => k.Contains('.'))
            .Select(k => k[..k.IndexOf('.')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
