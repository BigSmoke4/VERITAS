using Microsoft.EntityFrameworkCore;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Authorization.Application;

public sealed record GraphNode(string Id, string Label, string Type);
public sealed record GraphEdge(string FromId, string ToId, string Label);
public sealed record AccessGraphDto(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

public interface IAccessGraphQueryService
{
    Task<AccessGraphDto?> BuildForUserAsync(Guid userId, CancellationToken ct = default);
    Task<AccessGraphDto?> BuildForResourceAsync(Guid resourceId, CancellationToken ct = default);
}

/// <summary>
/// Single source of truth for graph construction, shared by the JSON API
/// (AccessGraphController) and the Razor UI (Views/AccessGraph) — so the two
/// can never show different data for the same query.
/// </summary>
public sealed class AccessGraphQueryService : IAccessGraphQueryService
{
    private readonly VeritasDbContext _db;
    public AccessGraphQueryService(VeritasDbContext db) => _db = db;

    public async Task<AccessGraphDto?> BuildForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        var nodes = new List<GraphNode> { new(user.Id.ToString(), user.DisplayName ?? user.UserName ?? user.Id.ToString(), "User") };
        var edges = new List<GraphEdge>();

        var roles = await _db.UserRoles2
            .Where(ur => ur.UserId == userId)
            .Include(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .ToListAsync(ct);

        foreach (var userRole in roles)
        {
            var roleNodeId = $"role:{userRole.RoleId}";
            nodes.Add(new GraphNode(roleNodeId, userRole.Role.Name, "Role"));
            edges.Add(new GraphEdge(user.Id.ToString(), roleNodeId, "has role"));

            foreach (var rp in userRole.Role.RolePermissions)
            {
                var permNodeId = $"perm:{rp.PermissionId}";
                if (nodes.All(n => n.Id != permNodeId))
                    nodes.Add(new GraphNode(permNodeId, rp.Permission.Key, "Permission"));
                edges.Add(new GraphEdge(roleNodeId, permNodeId, "grants"));
            }
        }

        return new AccessGraphDto(nodes, edges);
    }

    public async Task<AccessGraphDto?> BuildForResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        var resource = await _db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId, ct);
        if (resource is null) return null;

        var nodes = new List<GraphNode> { new(resource.Id.ToString(), resource.Name, "Resource") };
        var edges = new List<GraphEdge>();

        var activeGrants = await _db.Set<PrivilegedAccess.Domain.TemporaryGrant>()
            .Where(g => g.ResourceId == resourceId && !g.Revoked && g.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .ToListAsync(ct);

        foreach (var grant in activeGrants)
        {
            var userNodeId = grant.UserId.ToString();
            if (nodes.All(n => n.Id != userNodeId))
                nodes.Add(new GraphNode(userNodeId, userNodeId, "User"));
            edges.Add(new GraphEdge(userNodeId, resource.Id.ToString(), $"temporary grant: {grant.PermissionKey}"));
        }

        return new AccessGraphDto(nodes, edges);
    }
}
