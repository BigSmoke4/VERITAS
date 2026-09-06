using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.ResourceManagement.Domain;

public class Application : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = default!;
    public string Owner { get; set; } = default!;
    public string Environment { get; set; } = "production";
    public string Status { get; set; } = "ACTIVE";

    public List<Resource> Resources { get; set; } = new();
}

public class Resource : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Name { get; set; } = default!;
    public string ResourceType { get; set; } = default!;

    /// <summary>PUBLIC / INTERNAL / CONFIDENTIAL / HIGHLY_CONFIDENTIAL</summary>
    public string Classification { get; set; } = "INTERNAL";
    public string? OwnerDepartment { get; set; }
    public string Environment { get; set; } = "production";
}
