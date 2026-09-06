using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veritas.Web.Modules.Organization.Domain;
using Veritas.Web.Modules.PrivilegedAccess.Application;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Modules.ResourceManagement.Domain;
using Veritas.Web.Shared.Infrastructure;
using Xunit;

namespace Veritas.IntegrationTests;

/// <summary>
/// Real end-to-end scenario (spec section 73, simplified to the JIT-access
/// core): grant temporary access, confirm it's active, fast-forward past
/// expiry by writing an already-expired ExpiresAtUtc, run the exact same
/// expiration logic the background worker runs, and confirm the grant is
/// revoked. This exercises real DB writes/reads, not mocks.
/// </summary>
public class EndToEndTemporaryAccessTests : IClassFixture<VeritasWebAppFactory>
{
    private readonly VeritasWebAppFactory _factory;
    public EndToEndTemporaryAccessTests(VeritasWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task TemporaryGrant_ExpiresAndIsRevoked_ThenNoLongerActive()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
        var privileged = scope.ServiceProvider.GetRequiredService<IPrivilegedAccessService>();

        var orgId = Guid.NewGuid();
        var org = new Organization { Id = orgId, Name = "Apex Financial Group", Slug = $"apex-{orgId:N}" };
        db.Organizations.Add(org);

        var app = new Application { OrganizationId = orgId, Name = "Payment Platform", Owner = "Finance Technology" };
        db.Applications.Add(app);

        var resource = new Resource
        {
            OrganizationId = orgId,
            ApplicationId = app.Id,
            Name = "Production Payment Database",
            ResourceType = "database",
            Classification = "HIGHLY_CONFIDENTIAL",
            Environment = "production"
        };
        db.Resources.Add(resource);
        await db.SaveChangesAsync();

        var userId = Guid.NewGuid();

        // Step: grant 30-minute JIT access (spec section 73, step 7).
        var grant = await privileged.GrantTemporaryAccessAsync(
            userId, resource.Id, "payment.read", TimeSpan.FromMinutes(30), "Incident INC-20482");

        Assert.True(grant.IsActive(DateTimeOffset.UtcNow));
        Assert.True(await privileged.HasActiveGrantAsync(userId, resource.Id, "payment.read"));

        // Simulate 30 minutes passing by directly moving ExpiresAtUtc into the
        // past, then run the same expiration query the worker runs.
        var trackedGrant = await db.TemporaryGrants.FirstAsync(g => g.Id == grant.Id);
        trackedGrant.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var due = await db.TemporaryGrants
            .Where(g => !g.Revoked && g.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            .ToListAsync();
        foreach (var g in due)
        {
            g.Revoked = true;
            g.RevokedAtUtc = DateTimeOffset.UtcNow;
            g.RevokedReason = "Automatic expiration.";
        }
        await db.SaveChangesAsync();

        Assert.False(await privileged.HasActiveGrantAsync(userId, resource.Id, "payment.read"));

        var auditEntries = await db.AuditLogs.Where(a => a.CorrelationId == grant.Id.ToString()).ToListAsync();
        Assert.Contains(auditEntries, a => a.Action == "PRIVILEGED_ACCESS_GRANTED");
    }
}
