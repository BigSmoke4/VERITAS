using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veritas.Web.Infrastructure.Seeding;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.Organization.Domain;
using Veritas.Web.Shared.Infrastructure;
using Xunit;

namespace Veritas.IntegrationTests;

/// <summary>
/// Proves the seeder isn't just inserting inert rows: an authorize call
/// against the seeded "Production Payment Database" with high device trust
/// and a "read" action should actually resolve to ALLOW via the seeded
/// policy's rule 3, and running the seeder twice must not create duplicate
/// organizations (idempotency, per the seeder's own doc comment).
/// </summary>
public class DemoDataSeederTests : IClassFixture<VeritasWebAppFactory>
{
    private readonly VeritasWebAppFactory _factory;
    public DemoDataSeederTests(VeritasWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task SeededPolicy_AllowsActiveUserWithHighTrust_ToReadPaymentDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
        await seeder.SeedAsync();
        // Running it again must be a no-op, not a duplicate-key failure.
        await seeder.SeedAsync();

        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var org = await db.Organizations.IgnoreQueryFilters().FirstAsync(o => o.Slug == "apex-financial-group");
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.OrganizationId == org.Id && u.DisplayName == "Sarah Khan");
        var resource = await db.Resources.IgnoreQueryFilters().FirstAsync(r => r.OrganizationId == org.Id && r.Name == "Production Payment Database");

        // AuthorizationService resolves the acting tenant from ITenantContext,
        // which in production comes from an HTTP request's org_id claim. This
        // test calls the service directly with no HTTP request in flight, so
        // it sets the AsyncLocal-backed TestTenantContext explicitly instead.
        TestTenantContext.Current.Value = org.Id;

        var authorizationService = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var outcome = await authorizationService.AuthorizeAsync(new AuthorizationRequest
        {
            SubjectUserId = user.Id.ToString(),
            ResourceId = resource.Id.ToString(),
            Action = "read",
            Environment = "production",
            DeviceTrust = "HIGH",
            AuthenticationStrength = "STRONG"
        });

        Assert.Equal(AuthorizationDecisionResult.Allow, outcome.Result);
    }
}
