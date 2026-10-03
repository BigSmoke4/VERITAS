using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.RoleManagement.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.RoleManagement.Application;

public sealed record SoDViolation(string PermissionKeyA, string PermissionKeyB, string Description);

public interface ISeparationOfDutiesEvaluator
{
    /// <summary>Checks whether granting `role` to `userId` would create a conflict
    /// with any permission the user already effectively holds through another role.</summary>
    Task<SoDViolation?> CheckAsync(Guid userId, Guid candidateRoleId, CancellationToken ct = default);

    /// <summary>
    /// Same rule applied to a single permission key — used by the access-request
    /// path, where a user asks for a permission directly rather than for a role
    /// (spec section 74: a Finance Manager holding payment.create asking for
    /// payment.approve must be refused at request time).
    /// </summary>
    Task<SoDViolation?> CheckPermissionAsync(Guid userId, string candidatePermissionKey, CancellationToken ct = default);
}

/// <summary>
/// Real runtime enforcement: called from role-assignment (not just displayed
/// in a report). A violation throws before the UserRole row is ever written,
/// per spec section 23 — "a user attempting to violate this rule receives
/// DENIED", not a warning shown after the fact.
/// </summary>
public sealed class SeparationOfDutiesEvaluator : ISeparationOfDutiesEvaluator
{
    private readonly VeritasDbContext _db;

    public SeparationOfDutiesEvaluator(VeritasDbContext db) => _db = db;

    public async Task<SoDViolation?> CheckAsync(Guid userId, Guid candidateRoleId, CancellationToken ct = default)
    {
        var existingPermissionKeys = await _db.UserRoles2
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Key))
            .Distinct()
            .ToListAsync(ct);

        var candidatePermissionKeys = await _db.RolePermissions
            .Where(rp => rp.RoleId == candidateRoleId)
            .Select(rp => rp.Permission.Key)
            .Distinct()
            .ToListAsync(ct);

        var conflictRules = await _db.SoDConflictRules.AsNoTracking().ToListAsync(ct);

        foreach (var rule in conflictRules)
        {
            var hasA = existingPermissionKeys.Contains(rule.PermissionKeyA) || candidatePermissionKeys.Contains(rule.PermissionKeyA);
            var hasB = existingPermissionKeys.Contains(rule.PermissionKeyB) || candidatePermissionKeys.Contains(rule.PermissionKeyB);

            if (hasA && hasB)
                return new SoDViolation(rule.PermissionKeyA, rule.PermissionKeyB, rule.Description);
        }

        return null;
    }

    public async Task<SoDViolation?> CheckPermissionAsync(Guid userId, string candidatePermissionKey, CancellationToken ct = default)
    {
        var existingPermissionKeys = await _db.UserRoles2
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Key))
            .Distinct()
            .ToListAsync(ct);

        var conflictRules = await _db.SoDConflictRules.AsNoTracking().ToListAsync(ct);

        foreach (var rule in conflictRules)
        {
            var candidateIsA = string.Equals(rule.PermissionKeyA, candidatePermissionKey, StringComparison.OrdinalIgnoreCase);
            var candidateIsB = string.Equals(rule.PermissionKeyB, candidatePermissionKey, StringComparison.OrdinalIgnoreCase);
            if (!candidateIsA && !candidateIsB) continue;

            var counterpart = candidateIsA ? rule.PermissionKeyB : rule.PermissionKeyA;
            if (existingPermissionKeys.Contains(counterpart, StringComparer.OrdinalIgnoreCase))
                return new SoDViolation(rule.PermissionKeyA, rule.PermissionKeyB, rule.Description);
        }

        return null;
    }
}

public sealed record RoleAssignmentOutcome(bool Succeeded, string? Error);

public interface IRoleAssignmentService
{
    /// <summary>
    /// The single write path for UserRole rows. Returns a failed outcome (and
    /// audits it) when Separation-of-Duties would be violated, rather than
    /// throwing — callers surface the conflict to the user verbatim.
    /// </summary>
    Task<RoleAssignmentOutcome> AssignRoleAsync(Guid userId, Guid roleId, DateTimeOffset? expiresAtUtc, CancellationToken ct = default);
}

/// <summary>The only entry point that should write UserRole rows — enforces SoD every time, so there's no code path that bypasses it.</summary>
public sealed class RoleAssignmentService : IRoleAssignmentService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ISeparationOfDutiesEvaluator _sod;
    private readonly IAuditService _audit;

    public RoleAssignmentService(VeritasDbContext db, ITenantContext tenant, ISeparationOfDutiesEvaluator sod, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _sod = sod;
        _audit = audit;
    }

    public async Task<RoleAssignmentOutcome> AssignRoleAsync(Guid userId, Guid roleId, DateTimeOffset? expiresAtUtc, CancellationToken ct = default)
    {
        var alreadyHeld = await _db.UserRoles2
            .AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId
                            && (ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > DateTimeOffset.UtcNow), ct);
        if (alreadyHeld)
            return new RoleAssignmentOutcome(false, "Role is already assigned to this user.");

        var violation = await _sod.CheckAsync(userId, roleId, ct);
        if (violation is not null)
        {
            await _audit.RecordAsync(
                action: "ROLE_ASSIGNMENT_DENIED_SOD",
                resourceId: roleId.ToString(),
                previousValue: null,
                newValue: $"{violation.PermissionKeyA} conflicts with {violation.PermissionKeyB}",
                decisionId: null,
                correlationId: userId.ToString(),
                ct: ct);

            return new RoleAssignmentOutcome(false,
                $"Separation-of-Duties violation. Conflicting permissions: {violation.PermissionKeyA}, {violation.PermissionKeyB}");
        }

        _db.UserRoles2.Add(new UserRole
        {
            OrganizationId = _tenant.OrganizationId,
            UserId = userId,
            RoleId = roleId,
            ExpiresAtUtc = expiresAtUtc
        });
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("ROLE_ASSIGNED", roleId.ToString(), null, null, null, userId.ToString(), ct);
        return new RoleAssignmentOutcome(true, null);
    }
}
