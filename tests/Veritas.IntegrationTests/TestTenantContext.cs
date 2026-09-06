using Veritas.Web.Shared.Domain;

namespace Veritas.IntegrationTests;

/// <summary>
/// Test double for ITenantContext. The production HttpTenantContext reads
/// org_id from an authenticated HTTP principal, which doesn't exist when a
/// test calls an application service directly (no HTTP request in flight).
/// This uses an AsyncLocal so each test can set its own tenant for the
/// duration of its async call chain without tests interfering with each
/// other, and without needing to fake an HttpContext just to exercise
/// tenant-scoped services.
/// </summary>
public sealed class TestTenantContext : ITenantContext
{
    public static readonly AsyncLocal<Guid> Current = new();

    public Guid OrganizationId => Current.Value;
    public bool IsResolved => Current.Value != Guid.Empty;
}
