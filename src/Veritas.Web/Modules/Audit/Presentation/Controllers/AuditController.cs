using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veritas.Web.Modules.Audit.Application;

namespace Veritas.Web.Modules.Audit.Presentation.Controllers;

public sealed class AuditExplorerViewModel
{
    public required IReadOnlyList<AuditRow> Items { get; init; }
    public required int Total { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required IReadOnlyList<string> AvailableActions { get; init; }
    public required AuditQuery Query { get; init; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

[Authorize]
[EnableRateLimiting("audit-api")]
public sealed class AuditController : Controller
{
    private readonly IAuditQueryService _audit;

    public AuditController(IAuditQueryService audit) => _audit = audit;

    [HttpGet]
    public async Task<IActionResult> Index(
        string? action, Guid? actorUserId, string? resourceId, string? correlationId,
        string? ip, DateTimeOffset? from, DateTimeOffset? to, int page = 1, int pageSize = 25,
        CancellationToken ct = default)
    {
        var query = new AuditQuery(action, actorUserId, resourceId, correlationId, null, ip, from, to, page, pageSize);
        var (items, total) = await _audit.SearchAsync(query, ct);

        return View(new AuditExplorerViewModel
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = Math.Clamp(pageSize, 1, 100),
            AvailableActions = await _audit.ListDistinctActionsAsync(ct),
            Query = query
        });
    }

    /// <summary>CSV export of the current filter, capped server-side.</summary>
    [HttpGet]
    public async Task<IActionResult> Export(
        string? action, Guid? actorUserId, string? resourceId, string? correlationId,
        string? ip, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var query = new AuditQuery(action, actorUserId, resourceId, correlationId, null, ip, from, to, 1, 100);
        var rows = await _audit.ExportAsync(query, maxRows: 5000, ct);

        var csv = new StringBuilder();
        csv.AppendLine("timestamp_utc,action,actor,resource_id,resource,previous_value,new_value,decision_id,correlation_id,ip");

        foreach (var row in rows)
        {
            csv.AppendLine(string.Join(',',
                Csv(row.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                Csv(row.Action),
                Csv(row.ActorName ?? row.ActorUserId?.ToString()),
                Csv(row.ResourceId),
                Csv(row.ResourceLabel),
                Csv(row.PreviousValue),
                Csv(row.NewValue),
                Csv(row.DecisionId?.ToString()),
                Csv(row.CorrelationId),
                Csv(row.Ip)));
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"veritas-audit-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    public async Task<IActionResult> Decision(Guid id, CancellationToken ct)
    {
        var explanation = await _audit.GetDecisionAsync(id, ct);
        if (explanation is null) return NotFound();
        return View(explanation);
    }

    /// <summary>Escapes and quotes a CSV field, and neutralises formula injection.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@'))
            value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
