namespace Veritas.Web.Modules.Audit.Application;

public interface IAuditService
{
    Task RecordAsync(
        string action,
        string? resourceId,
        string? previousValue,
        string? newValue,
        Guid? decisionId,
        string correlationId,
        CancellationToken ct = default);
}
