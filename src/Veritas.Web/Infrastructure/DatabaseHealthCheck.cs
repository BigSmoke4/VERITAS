using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Infrastructure;

/// <summary>
/// Readiness probe that performs a real round-trip to PostgreSQL. Reports
/// Degraded (not Unhealthy) when the database is briefly unreachable, so a
/// transient blip does not get an instance killed by the orchestrator.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly VeritasDbContext _db;
    public DatabaseHealthCheck(VeritasDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL reachable.")
                : HealthCheckResult.Degraded("PostgreSQL did not confirm connectivity.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("PostgreSQL connectivity check failed.", ex);
        }
    }
}
