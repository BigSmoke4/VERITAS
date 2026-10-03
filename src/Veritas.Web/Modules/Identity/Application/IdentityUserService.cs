using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.RoleManagement.Application;
using Veritas.Web.Shared.Application.AccessGrants;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Identity.Application;

public sealed record UserListItem(
    Guid Id, string DisplayName, string Email, string LifecycleState, string RiskLevel,
    string Clearance, string? Department, string? JobTitle, DateTimeOffset? LastLoginAtUtc, int RoleCount);

public sealed record UserDetail(
    Guid Id, string DisplayName, string Email, string LifecycleState, string RiskLevel, string Clearance,
    string? JobTitle, string? Location, string? Country, string? Department,
    DateTimeOffset? LastLoginAtUtc, DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions,
    IReadOnlyList<ActiveGrantView> TemporaryGrants);

public sealed record CreateUserCommand(
    string DisplayName, string Email, string JobTitle, Guid? DepartmentId,
    string Clearance, string InitialPassword, IReadOnlyList<Guid> RoleIds);

public interface IIdentityUserService
{
    Task<(IReadOnlyList<UserListItem> Items, int Total)> SearchAsync(string? term, string? state, int page, int pageSize, CancellationToken ct = default);
    Task<UserDetail?> GetAsync(Guid userId, CancellationToken ct = default);
    Task<(bool Succeeded, Guid? UserId, IReadOnlyList<string> Errors)> CreateAsync(CreateUserCommand command, CancellationToken ct = default);
    Task<(bool Succeeded, IReadOnlyList<string> Errors)> UpdateAsync(Guid userId, string displayName, string jobTitle, Guid? departmentId, string clearance, string location, string country, CancellationToken ct = default);
    Task<RoleAssignmentOutcome> AssignRoleAsync(Guid userId, Guid roleId, DateTimeOffset? expiresAtUtc, CancellationToken ct = default);
    Task RevokeRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default);
}

public sealed record RoleAssignmentOutcome(bool Succeeded, string? Error);

/// <summary>
/// Identity administration. Role assignment goes through
/// <see cref="IRoleAssignmentService"/> so Separation-of-Duties is enforced on
/// every path — there is no "admin UI" back door around it.
/// </summary>
public sealed class IdentityUserService : IIdentityUserService
{
    private readonly VeritasDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;
    private readonly IRoleAssignmentService _roleAssignment;
    private readonly IRbacEvaluator _rbac;
    private readonly ITemporaryGrantReader _grants;

    public IdentityUserService(
        VeritasDbContext db,
        UserManager<ApplicationUser> userManager,
        ITenantContext tenant,
        IAuditService audit,
        IRoleAssignmentService roleAssignment,
        IRbacEvaluator rbac,
        ITemporaryGrantReader grants)
    {
        _db = db;
        _userManager = userManager;
        _tenant = tenant;
        _audit = audit;
        _roleAssignment = roleAssignment;
        _rbac = rbac;
        _grants = grants;
    }

    public async Task<(IReadOnlyList<UserListItem> Items, int Total)> SearchAsync(
        string? term, string? state, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.Users.AsNoTracking().Where(u => u.OrganizationId == _tenant.OrganizationId && !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            query = query.Where(u => u.DisplayName.Contains(t) || u.Email!.Contains(t));
        }

        if (!string.IsNullOrWhiteSpace(state))
            query = query.Where(u => u.LifecycleState == state);

        var total = await query.CountAsync(ct);

        var departmentIds = await _db.Departments.AsNoTracking()
            .Select(d => new { d.Id, d.Name }).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var roleCounts = await _db.UserRoles2.AsNoTracking()
            .GroupBy(ur => ur.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var rows = await query
            .OrderBy(u => u.DisplayName)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.LifecycleState, u.RiskLevel, u.Clearance, u.DepartmentId, u.JobTitle, u.LastLoginAtUtc })
            .ToListAsync(ct);

        var items = rows.Select(u => new UserListItem(
            u.Id, u.DisplayName, u.Email ?? string.Empty, u.LifecycleState, u.RiskLevel, u.Clearance,
            u.DepartmentId is not null && departmentIds.TryGetValue(u.DepartmentId.Value, out var dn) ? dn : null,
            u.JobTitle, u.LastLoginAtUtc,
            roleCounts.TryGetValue(u.Id, out var rc) ? rc : 0)).ToList();

        return (items, total);
    }

    public async Task<UserDetail?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;

        var department = user.DepartmentId is null
            ? null
            : await _db.Departments.AsNoTracking().Where(d => d.Id == user.DepartmentId).Select(d => d.Name).FirstOrDefaultAsync(ct);

        var effective = await _rbac.ResolveAsync(userId, ct);
        var grants = await _grants.GetGrantsForUserAsync(userId, ct);

        return new UserDetail(
            user.Id, user.DisplayName, user.Email ?? string.Empty, user.LifecycleState, user.RiskLevel, user.Clearance,
            user.JobTitle, user.Location, user.Country, department, user.LastLoginAtUtc, user.CreatedAtUtc,
            effective.RoleNames, effective.StaticPermissionToRole.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            grants);
    }

    public async Task<(bool Succeeded, Guid? UserId, IReadOnlyList<string> Errors)> CreateAsync(CreateUserCommand command, CancellationToken ct = default)
    {
        var user = new ApplicationUser
        {
            OrganizationId = _tenant.OrganizationId,
            DisplayName = command.DisplayName.Trim(),
            UserName = command.Email.Trim().ToLowerInvariant(),
            Email = command.Email.Trim().ToLowerInvariant(),
            EmailConfirmed = false,
            JobTitle = command.JobTitle,
            DepartmentId = command.DepartmentId,
            Clearance = command.Clearance,
            // Invited until the user confirms their email — see UserLifecycleState.
            LifecycleState = UserLifecycleState.Invited
        };

        var created = await _userManager.CreateAsync(user, command.InitialPassword);
        if (!created.Succeeded)
            return (false, null, created.Errors.Select(e => e.Description).ToList());

        var errors = new List<string>();
        foreach (var roleId in command.RoleIds)
        {
            var outcome = await _roleAssignment.AssignRoleAsync(user.Id, roleId, null, ct);
            if (!outcome.Succeeded) errors.Add(outcome.Error!);
        }

        await _audit.RecordAsync("USER_CREATED", user.Id.ToString(), null, user.LifecycleState, null, user.Email!, ct);
        return (true, user.Id, errors);
    }

    public async Task<(bool Succeeded, IReadOnlyList<string> Errors)> UpdateAsync(
        Guid userId, string displayName, string jobTitle, Guid? departmentId, string clearance,
        string location, string country, CancellationToken ct = default)
    {
        var user = await _userManager.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return (false, new[] { "User not found in this tenant." });

        var previous = $"{user.DisplayName}|{user.JobTitle}|{user.Clearance}";

        user.DisplayName = displayName.Trim();
        user.JobTitle = jobTitle;
        user.DepartmentId = departmentId;
        user.Clearance = clearance;
        user.Location = location;
        user.Country = country;

        var updated = await _userManager.UpdateAsync(user);
        if (!updated.Succeeded)
            return (false, updated.Errors.Select(e => e.Description).ToList());

        await _audit.RecordAsync("USER_UPDATED", user.Id.ToString(), previous,
            $"{user.DisplayName}|{user.JobTitle}|{user.Clearance}", null, "profile", ct);

        return (true, Array.Empty<string>());
    }

    public async Task<RoleAssignmentOutcome> AssignRoleAsync(Guid userId, Guid roleId, DateTimeOffset? expiresAtUtc, CancellationToken ct = default) =>
        await _roleAssignment.AssignRoleAsync(userId, roleId, expiresAtUtc, ct);

    public async Task RevokeRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        var grant = await _db.UserRoles2.FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct);
        if (grant is null) return;

        _db.UserRoles2.Remove(grant);
        await _db.SaveChangesAsync(ct);

        await _userManager.UpdateSecurityStampAsync(
            await _userManager.Users.FirstAsync(u => u.Id == userId, ct));

        await _audit.RecordAsync("ROLE_REVOKED", roleId.ToString(), null, null, null, userId.ToString(), ct);
    }
}
