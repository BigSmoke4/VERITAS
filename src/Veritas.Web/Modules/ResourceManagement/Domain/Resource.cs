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

    /// <summary>
    /// Left half of the <c>resource.action</c> permission key (spec section 9).
    /// Storing it on the resource keeps the action -&gt; permission mapping in data
    /// rather than in a hardcoded switch inside the authorization path.
    /// </summary>
    public string PermissionKeyPrefix { get; set; } = "resource";

    public Application? Application { get; set; }
}

/// <summary>Resource taxonomy rows so "database"/"queue"/"api" are managed, not free text.</summary>
public class ResourceTypeDefinition : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Key { get; set; } = default!;
    public string Description { get; set; } = string.Empty;

    /// <summary>Default classification applied to newly created resources of this type.</summary>
    public string DefaultClassification { get; set; } = "INTERNAL";
}

/// <summary>A named, permission-bearing grouping of resources (spec section 5).</summary>
public class ResourceGroup : AuditableEntity, ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = default!;
    public string Description { get; set; } = string.Empty;
    public Guid? ApplicationId { get; set; }
}
