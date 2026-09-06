using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veritas.Web.Modules.Organization.Domain;
using Veritas.Web.Modules.ResourceManagement.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;
using Xunit;

namespace Veritas.IntegrationTests;

/// <summary>
/// Proves tenant isolation against a real PostgreSQL instance, not a mock:
/// two organizations each get a resource; a DbContext scoped to org A's
/// tenant context must never see org B's row, even with an unfiltered LINQ
/// query, because the global query filter runs at the provider level.
/// </summary>
public class TenantIsolationTests : IClassFixture<VeritasWebAppFactory>
{
    private readonly VeritasWebAppFactory _factory;
    public TenantIsolationTests(VeritasWebAppFactory factory) => _factory = factory;

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid orgId) => OrganizationId = orgId;
        public Guid OrganizationId { get; }
        public bool IsResolved => true;
    }

    [Fact]
    public async Task ResourcesFromOtherOrganization_AreNeverReturned()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
            db.Organizations.Add(new Organization { Id = orgA, Name = "Org A", Slug = $"org-a-{orgA:N}" });
            db.Organizations.Add(new Organization { Id = orgB, Name = "Org B", Slug = $"org-b-{orgB:N}" });
            var appA = new Application { Id = Guid.NewGuid(), OrganizationId = orgA, Name = "App A", Owner = "Team A" };
            var appB = new Application { Id = Guid.NewGuid(), OrganizationId = orgB, Name = "App B", Owner = "Team B" };
            db.Applications.AddRange(appA, appB);
            db.Resources.Add(new Resource { OrganizationId = orgA, ApplicationId = appA.Id, Name = "Org A Secret DB", ResourceType = "database" });
            db.Resources.Add(new Resource { OrganizationId = orgB, ApplicationId = appB.Id, Name = "Org B Secret DB", ResourceType = "database" });
            await db.SaveChangesAsync();
        }

        var connectionString = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");

        using var scopedAsOrgA = new ServiceCollection()
            .AddDbContext<VeritasDbContext>(o => o.UseNpgsql(connectionString))
            .AddSingleton<ITenantContext>(new FixedTenantContext(orgA))
            .BuildServiceProvider();

        var scopedDb = scopedAsOrgA.GetRequiredService<VeritasDbContext>();
        var visibleResources = await scopedDb.Resources.ToListAsync();

        Assert.Single(visibleResources);
        Assert.Equal("Org A Secret DB", visibleResources[0].Name);
        Assert.DoesNotContain(visibleResources, r => r.Name == "Org B Secret DB");
    }
}
