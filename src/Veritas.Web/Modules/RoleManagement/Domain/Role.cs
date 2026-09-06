using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.RoleManagement.Domain;

public class Role : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = default!;
    public string Description { get; set; } = string.Empty;
    public bool IsSystemRole { get; set; }

    public List<RolePermission> RolePermissions { get; set; } = new();
}

public class Permission : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>Follows the "resource.action" convention, e.g. "payment.approve".</summary>
    public string Key { get; set; } = default!;
    public string Description { get; set; } = string.Empty;
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = default!;
    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = default!;
}

public class UserRole : ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = default!;
    public DateTimeOffset AssignedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

/// <summary>
/// Separation-of-Duties: pairs of permission keys that must never both be held
/// by the same user at the same time (see SeparationOfDutiesEvaluator).
/// </summary>
public class SoDConflictRule : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string PermissionKeyA { get; set; } = default!;
    public string PermissionKeyB { get; set; } = default!;
    public string Description { get; set; } = string.Empty;
}
