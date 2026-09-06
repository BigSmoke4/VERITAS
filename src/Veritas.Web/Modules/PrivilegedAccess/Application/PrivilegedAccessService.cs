using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.PrivilegedAccess.Application;

public interface IPrivilegedAccessService
{
    Task<TemporaryGrant> GrantTemporaryAccessAsync(Guid userId, Guid resourceId, string permissionKey, TimeSpan duration, string reason, CancellationToken ct = default);
    Task RevokeAsync(Guid grantId, string reason, CancellationToken ct = default);
    Task<bool> HasActiveGrantAsync(Guid userId, Guid resourceId, string permissionKey, CancellationToken ct = default);
}

public sealed class PrivilegedAccessService : IPrivilegedAccessService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public PrivilegedAccessService(VeritasDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<TemporaryGrant> GrantTemporaryAccessAsync(
        Guid userId, Guid resourceId, string permissionKey, TimeSpan duration, string reason, CancellationToken ct = default)
    {
        var grant = new TemporaryGrant
        {
            OrganizationId = _tenant.OrganizationId,
            UserId = userId,
            ResourceId = resourceId,
            PermissionKey = permissionKey,
            Reason = reason,
            StartAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow + duration
        };

        _db.Set<TemporaryGrant>().Add(grant);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("PRIVILEGED_ACCESS_GRANTED", resourceId.ToString(), null, permissionKey, null, grant.Id.ToString(), ct);
        return grant;
    }

    public async Task RevokeAsync(Guid grantId, string reason, CancellationToken ct = default)
    {
        var grant = await _db.Set<TemporaryGrant>().FirstOrDefaultAsync(g => g.Id == grantId, ct)
            ?? throw new InvalidOperationException("Grant not found in this tenant.");

        grant.Revoked = true;
        grant.RevokedAtUtc = DateTimeOffset.UtcNow;
        grant.RevokedReason = reason;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("TEMPORARY_ACCESS_REVOKED", grant.ResourceId.ToString(), null, reason, null, grant.Id.ToString(), ct);
    }

    public async Task<bool> HasActiveGrantAsync(Guid userId, Guid resourceId, string permissionKey, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.Set<TemporaryGrant>().AnyAsync(g =>
            g.UserId == userId &&
            g.ResourceId == resourceId &&
            g.PermissionKey == permissionKey &&
            !g.Revoked &&
            g.ExpiresAtUtc > now, ct);
    }
}
