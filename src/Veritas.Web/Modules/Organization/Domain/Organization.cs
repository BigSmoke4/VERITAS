using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.Organization.Domain;

public class Organization : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string Status { get; set; } = OrganizationStatus.Active;

    public List<Department> Departments { get; set; } = new();
}

public static class OrganizationStatus
{
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
}

public class Department : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = default!;
    public Guid? ParentDepartmentId { get; set; }
}
