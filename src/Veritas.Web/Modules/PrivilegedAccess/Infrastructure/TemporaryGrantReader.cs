using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Shared.Application.AccessGrants;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.PrivilegedAccess.Infrastructure;

/// <summary>
/// The single place outside PrivilegedAccess that is allowed to read
/// TemporaryGrant rows, and it only ever projects them into
/// <see cref="ActiveGrantView"/> — never an entity another module could mutate.
/// </summary>
/// <remarks>
/// Filtering and ordering are always applied to the <see cref="TemporaryGrant"/>
/// query, and the projection into <see cref="ActiveGrantView"/> is the very last
/// operation before materialisation. That ordering is not a stylistic preference:
/// a <c>Where</c> or <c>OrderBy</c> chained after a constructor projection makes EF
/// Core inline the record constructor into the predicate, which it cannot translate
/// to SQL and which fails at runtime with
/// <c>InvalidOperationException: The LINQ expression ... could not be translated</c>.
/// Compiling clean is not enough here — only executing against PostgreSQL proves it.
/// </remarks>
public sealed class TemporaryGrantReader : ITemporaryGrantReader
{
    private readonly VeritasDbContext _db;
    public TemporaryGrantReader(VeritasDbContext db) => _db = db;

    public async Task<IReadOnlyList<ActiveGrantView>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        await Project(Grants()
                .Where(g => g.UserId == userId)
                .OrderByDescending(g => g.StartAtUtc))
            .ToListAsync(ct);

    public async Task<ActiveGrantView?> GetMostRecentGrantAsync(Guid userId, Guid resourceId, string permissionKey, CancellationToken ct = default) =>
        await Project(Grants()
                .Where(g => g.UserId == userId && g.ResourceId == resourceId && g.PermissionKey == permissionKey)
                .OrderByDescending(g => g.StartAtUtc))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ActiveGrantView>> GetActiveGrantsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await Project(Grants()
                .Where(g => g.UserId == userId && !g.Revoked && g.ExpiresAtUtc > now)
                .OrderByDescending(g => g.ExpiresAtUtc))
            .ToListAsync(ct);
    }

    private IQueryable<TemporaryGrant> Grants() => _db.Set<TemporaryGrant>().AsNoTracking();

    /// <summary>Must be the last query operation before materialisation — see remarks.</summary>
    private static IQueryable<ActiveGrantView> Project(IQueryable<TemporaryGrant> grants) =>
        grants.Select(g => new ActiveGrantView(
            g.Id, g.UserId, g.ResourceId, g.PermissionKey, g.StartAtUtc, g.ExpiresAtUtc,
            g.Revoked, g.RevokedAtUtc, g.RevokedReason, g.Reason));
}
