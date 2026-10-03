using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Shared.Infrastructure;

/// <summary>
/// Design-time context factory used by <c>dotnet ef migrations add</c> and
/// <c>dotnet ef database update</c>.
///
/// The EF tooling constructs the context outside the application's DI container, so
/// it needs a way to obtain one. This factory reads the connection string from the
/// environment (falling back to the same key <see cref="Program"/> binds, so a
/// migration generated locally matches one generated in CI) and supplies a
/// <see cref="ITenantContext"/> that is deliberately <em>unresolved</em>:
///
///   * Migration scaffolding never executes queries, so no tenant is ever needed.
///   * <see cref="VeritasDbContext"/>'s global query filters are registered against
///     the tenant accessor rather than a captured value, so the model this factory
///     produces is byte-identical to the runtime one. Passing a real organisation id
///     here would be misleading — it would suggest migrations are tenant-scoped,
///     and they are not.
///
/// The connection string is only used to open a connection for <c>database update</c>;
/// no data is read while generating a migration.
/// </summary>
public sealed class DesignTimeVeritasDbContextFactory : IDesignTimeDbContextFactory<VeritasDbContext>
{
    public const string ConnectionStringEnvironmentVariable = "VERITAS_DESIGNTIME_CONNECTIONSTRING";
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=veritas_design;Username=veritas;Password=veritas";

    public VeritasDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            connection = DefaultConnectionString;
            Console.WriteLine(
                $"[Veritas] {ConnectionStringEnvironmentVariable} is not set; using the design-time default '{DefaultConnectionString}'.");
        }

        // These options must stay equivalent to the ones Program.cs registers: a model
        // built under different conventions would produce migrations that do not match
        // the schema the running application expects.
        var options = new DbContextOptionsBuilder<VeritasDbContext>()
            .UseNpgsql(connection, npgsql =>
                npgsql.CommandTimeout(60)
                      .MaxBatchSize(64)
                      .EnableRetryOnFailure(maxRetryCount: 3))
            .Options;

        return new VeritasDbContext(options, new UnresolvedDesignTimeTenantContext());
    }

    /// <summary>
    /// Tenant context that reports itself as unresolved. Any code path that tried to
    /// scope a design-time query to a tenant would fail loudly instead of silently
    /// returning another tenant's rows.
    /// </summary>
    private sealed class UnresolvedDesignTimeTenantContext : ITenantContext
    {
        public Guid OrganizationId => Guid.Empty;
        public bool IsResolved => false;
    }
}
