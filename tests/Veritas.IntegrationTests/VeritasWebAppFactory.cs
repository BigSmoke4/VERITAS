using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Veritas.Web.Shared.Infrastructure;
using Xunit;

namespace Veritas.IntegrationTests;

/// <summary>
/// Boots the real app against real, ephemeral PostgreSQL and Redis containers
/// (Testcontainers) — no in-memory EF provider substitution, per spec
/// section 62 ("Use Testcontainers where appropriate"). Requires Docker to be
/// available on the machine running `dotnet test`.
/// </summary>
public sealed class VeritasWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("veritas_test")
        .WithUsername("veritas")
        .WithPassword("veritas_test_password")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _redis.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());

        builder.ConfigureServices(services =>
        {
            // Replace the HTTP-claim-based tenant context with an AsyncLocal
            // test double — see TestTenantContext for why.
            services.RemoveAll<Veritas.Web.Shared.Domain.ITenantContext>();
            services.AddScoped<Veritas.Web.Shared.Domain.ITenantContext, TestTenantContext>();

            using var scopeCheck = services.BuildServiceProvider();
            using var scope = scopeCheck.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VeritasDbContext>();
            db.Database.EnsureCreated(); // integration tests exercise the model directly; migrations are checked separately.
        });
    }
}
