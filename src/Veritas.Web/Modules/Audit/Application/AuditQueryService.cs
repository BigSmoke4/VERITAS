using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Audit.Application;

public sealed record AuditQuery(
    string? Action, Guid? ActorUserId, string? ResourceId, string? CorrelationId,
    Guid? DecisionId, string? Ip, DateTimeOffset? From, DateTimeOffset? To,
    int Page = 1, int PageSize = 25);

public sealed record AuditRow(
    Guid Id, string Action, Guid? ActorUserId, string? ActorName, string? ResourceId,
    string? ResourceLabel, string? PreviousValue, string? NewValue,
    Guid? DecisionId, string? CorrelationId, string? Ip, DateTimeOffset TimestampUtc);

public sealed record DecisionExplanationView(
    Guid DecisionId, string PublicDecisionId, string Subject, string? SubjectName,
    string ResourceLabel, string Action, string Environment, string Result,
    int? RiskScore, string? RiskLevel, string? PolicyName, int? PolicyVersionNumber,
    string? RequiredPermissionKey, DateTimeOffset EvaluatedAtUtc, DateTimeOffset? ExpiresAtUtc,
    string? CorrelationId, IReadOnlyList<string> Reasons, IReadOnlyList<DecisionCheckView> Checks);

public sealed record DecisionCheckView(string Code, string Description, bool Passed, string? Detail);

public interface IAuditQueryService
{
    Task<(IReadOnlyList<AuditRow> Items, int Total)> SearchAsync(AuditQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<AuditRow>> ExportAsync(AuditQuery query, int maxRows, CancellationToken ct = default);
    Task<DecisionExplanationView?> GetDecisionAsync(Guid decisionId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListDistinctActionsAsync(CancellationToken ct = default);
}

/// <summary>
/// Server-side audit search (spec sections 29-30). Paging is mandatory and the
/// page size is clamped by AuditOptions.MaxPageSize, so no call path can pull an
/// unbounded result set into the browser. Export is separately capped.
/// </summary>
public sealed class AuditQueryService : IAuditQueryService
{
    private readonly VeritasDbContext _db;
    private readonly AuditOptions _options;

    public AuditQueryService(VeritasDbContext db, IOptions<AuditOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<(IReadOnlyList<AuditRow> Items, int Total)> SearchAsync(AuditQuery query, CancellationToken ct = default)
    {
        var filtered = ApplyFilters(query);
        var total = await filtered.CountAsync(ct);

        var pageSize = Math.Clamp(query.PageSize, 1, _options.MaxPageSize);
        var page = Math.Max(query.Page, 1);

        var rows = await filtered
            .OrderByDescending(a => a.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (await EnrichAsync(rows, ct), total);
    }

    public async Task<IReadOnlyList<AuditRow>> ExportAsync(AuditQuery query, int maxRows, CancellationToken ct = default)
    {
        var cap = Math.Min(maxRows, 10_000);
        var rows = await ApplyFilters(query)
            .OrderByDescending(a => a.TimestampUtc)
            .Take(cap)
            .ToListAsync(ct);
        return await EnrichAsync(rows, ct);
    }

    public async Task<IReadOnlyList<string>> ListDistinctActionsAsync(CancellationToken ct = default) =>
        await _db.AuditLogs.AsNoTracking()
            .Select(a => a.Action).Distinct()
            .OrderBy(a => a)
            .ToListAsync(ct);

    public async Task<DecisionExplanationView?> GetDecisionAsync(Guid decisionId, CancellationToken ct = default)
    {
        var decision = await _db.AuthorizationDecisions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == decisionId, ct);
        if (decision is null) return null;

        string? subjectName = null;
        if (Guid.TryParse(decision.SubjectUserId, out var subjectId))
        {
            subjectName = await _db.Users.AsNoTracking()
                .Where(u => u.Id == subjectId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        }

        var resourceLabel = await _db.Resources.AsNoTracking()
            .Where(r => r.Id.ToString() == decision.ResourceId).Select(r => r.Name).FirstOrDefaultAsync(ct)
            ?? decision.ResourceId;

        string? policyName = null;
        int? policyVersionNumber = null;
        if (decision.PolicyVersionId is not null)
        {
            var version = await _db.PolicyVersions.AsNoTracking()
                .Include(v => v.Policy)
                .FirstOrDefaultAsync(v => v.Id == decision.PolicyVersionId, ct);
            policyName = version?.Policy?.Name;
            policyVersionNumber = version?.VersionNumber;
        }

        return new DecisionExplanationView(
            decision.Id, $"DEC-{decision.Id:N}", decision.SubjectUserId, subjectName,
            resourceLabel, decision.Action, decision.Environment, decision.Result,
            decision.RiskScore, decision.RiskLevel, policyName, policyVersionNumber,
            decision.RequiredPermissionKey, decision.EvaluatedAtUtc, decision.ExpiresAtUtc,
            decision.CorrelationId,
            Deserialize(decision.ReasonsJson, json => System.Text.Json.JsonSerializer.Deserialize<List<string>>(json)),
            DeserializeChecks(decision.ChecksJson));
    }

    private IQueryable<AuditLog> ApplyFilters(AuditQuery query)
    {
        var q = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Action)) q = q.Where(a => a.Action == query.Action);
        if (query.ActorUserId is not null) q = q.Where(a => a.ActorUserId == query.ActorUserId);
        if (!string.IsNullOrWhiteSpace(query.ResourceId)) q = q.Where(a => a.ResourceId == query.ResourceId);
        if (!string.IsNullOrWhiteSpace(query.CorrelationId)) q = q.Where(a => a.CorrelationId == query.CorrelationId);
        if (query.DecisionId is not null) q = q.Where(a => a.DecisionId == query.DecisionId);
        if (!string.IsNullOrWhiteSpace(query.Ip)) q = q.Where(a => a.Ip == query.Ip);
        if (query.From is not null) q = q.Where(a => a.TimestampUtc >= query.From);
        if (query.To is not null) q = q.Where(a => a.TimestampUtc <= query.To);

        return q;
    }

    private async Task<IReadOnlyList<AuditRow>> EnrichAsync(IReadOnlyList<AuditLog> rows, CancellationToken ct)
    {
        var actorIds = rows.Where(r => r.ActorUserId is not null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var actors = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var resourceIds = rows.Where(r => r.ResourceId != null).Select(r => r.ResourceId!).Distinct().ToList();
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        if (resourceIds.Count > 0)
        {
            var named = await _db.Resources.AsNoTracking()
                .Select(r => new { IdText = r.Id.ToString(), r.Name }).ToListAsync(ct);
            foreach (var n in named.Where(n => resourceIds.Contains(n.IdText)))
                resources[n.IdText] = n.Name;
        }

        return rows.Select(a => new AuditRow(
            a.Id, a.Action, a.ActorUserId,
            a.ActorUserId is not null && actors.TryGetValue(a.ActorUserId.Value, out var an) ? an : null,
            a.ResourceId,
            a.ResourceId is not null && resources.TryGetValue(a.ResourceId, out var rn) ? rn : null,
            a.PreviousValue, a.NewValue, a.DecisionId, a.CorrelationId, a.Ip, a.TimestampUtc)).ToList();
    }

    private static IReadOnlyList<string> Deserialize(string json, Func<string, List<string>?> deserialize)
    {
        try { return deserialize(json) is { } list ? list : Array.Empty<string>(); }
        catch (System.Text.Json.JsonException) { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<DecisionCheckView> DeserializeChecks(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<DecisionCheckView>>(json)
                   is { } checks ? checks : Array.Empty<DecisionCheckView>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<DecisionCheckView>();
        }
    }
}
