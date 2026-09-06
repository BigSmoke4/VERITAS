using Microsoft.AspNetCore.Identity;
using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.Identity.Domain;

/// <summary>
/// User lifecycle: Invited -> PendingVerification -> Active -> Suspended -> Revoked.
/// Every transition must be written through UserLifecycleService so it is audited
/// (see Modules/Identity/Application/UserLifecycleService.cs).
/// </summary>
public class ApplicationUser : IdentityUser<Guid>, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string DisplayName { get; set; } = default!;
    public string LifecycleState { get; set; } = UserLifecycleState.Invited;
    public string RiskLevel { get; set; } = "LOW";
    public DateTimeOffset? LastLoginAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

public static class UserLifecycleState
{
    public const string Invited = "INVITED";
    public const string PendingVerification = "PENDING_VERIFICATION";
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Revoked = "REVOKED";

    public static readonly IReadOnlyDictionary<string, string[]> AllowedTransitions =
        new Dictionary<string, string[]>
        {
            [Invited] = new[] { PendingVerification, Revoked },
            [PendingVerification] = new[] { Active, Revoked },
            [Active] = new[] { Suspended, Revoked },
            [Suspended] = new[] { Active, Revoked },
            [Revoked] = Array.Empty<string>()
        };
}
