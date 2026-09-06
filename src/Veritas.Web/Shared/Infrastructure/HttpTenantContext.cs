using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Shared.Infrastructure;

/// <summary>
/// Resolves OrganizationId from the authenticated user's claims. Never trusts a
/// tenant id supplied in the request body/query string — that would allow a
/// caller to simply ask for another tenant's data.
/// </summary>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpTenantContext(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid OrganizationId
    {
        get
        {
            var claim = _accessor.HttpContext?.User.FindFirst("org_id")?.Value;
            return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
        }
    }

    public bool IsResolved => OrganizationId != Guid.Empty;
}
