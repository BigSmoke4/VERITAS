using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Veritas.Web.Modules.Identity.Domain;

namespace Veritas.Web.Modules.Identity.Infrastructure;

/// <summary>
/// Real claim issuance closing the previously-documented gap: HttpTenantContext
/// reads OrganizationId from an "org_id" claim on the authenticated principal,
/// but nothing was putting that claim there. This factory runs on every
/// sign-in (cookie auth) and adds org_id, plus lifecycle_state and
/// display_name, straight from the user's own row — no separate claims
/// table to keep in sync, no way for org_id to point at a tenant the user
/// doesn't actually belong to.
/// </summary>
public sealed class VeritasClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>
{
    public VeritasClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        Microsoft.Extensions.Options.IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor)
    {
    }

    public override async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
        var principal = await base.CreateAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;

        identity.AddClaim(new Claim("org_id", user.OrganizationId.ToString()));
        identity.AddClaim(new Claim("lifecycle_state", user.LifecycleState));
        identity.AddClaim(new Claim("display_name", user.DisplayName ?? user.UserName ?? user.Id.ToString()));

        return principal;
    }
}
