using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Audit.Application;

public sealed class AuditService : IAuditService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IHttpContextAccessor _http;

    public AuditService(VeritasDbContext db, ITenantContext tenant, IHttpContextAccessor http)
    {
        _db = db;
        _tenant = tenant;
        _http = http;
    }

    public async Task RecordAsync(
        string action,
        string? resourceId,
        string? previousValue,
        string? newValue,
        Guid? decisionId,
        string correlationId,
        CancellationToken ct = default)
    {
        var actorClaim = _http.HttpContext?.User.FindFirst("sub")?.Value;
        Guid? actorId = Guid.TryParse(actorClaim, out var parsed) ? parsed : null;

        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = _tenant.OrganizationId,
            ActorUserId = actorId,
            Action = action,
            ResourceId = resourceId,
            PreviousValue = previousValue,
            NewValue = newValue,
            DecisionId = decisionId,
            CorrelationId = correlationId,
            Ip = _http.HttpContext?.Connection.RemoteIpAddress?.ToString()
        });

        await _db.SaveChangesAsync(ct);
    }
}
