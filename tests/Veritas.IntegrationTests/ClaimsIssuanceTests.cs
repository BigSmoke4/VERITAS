using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Organization.Domain;
using Veritas.Web.Shared.Infrastructure;
using Xunit;

namespace Veritas.IntegrationTests;

/// <summary>
/// Closes the previously-documented gap: proves that signing a user's claims
/// principal (what actually happens at cookie sign-in) carries a real org_id
/// claim matching that user's own OrganizationId column — not a hardcoded or
/// missing value. This is the mechanism HttpTenantContext depends on for
/// every tenant-scoped query in the app.
/// </summary>
public class ClaimsIssuanceTests : IClassFixture<VeritasWebAppFactory>
{
    private readonly VeritasWebAppFactory _factory;
    public ClaimsIssuanceTests(VeritasWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task SignInClaimsPrincipal_ContainsOrgIdMatchingUsersOwnOrganization()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var claimsFactory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        var orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = orgId, Name = "Apex Financial Group", Slug = $"apex-{orgId:N}" });
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserName = $"jsmith-{orgId:N}@apex.example",
            Email = $"jsmith-{orgId:N}@apex.example",
            DisplayName = "John Smith",
            LifecycleState = UserLifecycleState.Active,
            EmailConfirmed = true
        };
        var createResult = await userManager.CreateAsync(user, "CorrectHorse!Battery9Staple");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));

        var principal = await claimsFactory.CreateAsync(user);

        var orgClaim = principal.FindFirst("org_id");
        Assert.NotNull(orgClaim);
        Assert.Equal(orgId.ToString(), orgClaim!.Value);

        var lifecycleClaim = principal.FindFirst("lifecycle_state");
        Assert.Equal(UserLifecycleState.Active, lifecycleClaim?.Value);
    }
}
