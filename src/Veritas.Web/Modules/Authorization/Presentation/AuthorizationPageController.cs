using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Shared.Infrastructure;
using VeritasApplication = Veritas.Web.Modules.Authorization.Application;

namespace Veritas.Web.Modules.Authorization.Presentation;

public sealed class AuthorizationIndexViewModel
{
    public IReadOnlyList<DecisionRow> Decisions { get; init; } = Array.Empty<DecisionRow>();
    public int Total { get; init; }
    public int Page { get; init; } = 1;
    public string? Result { get; init; }
}

public sealed record DecisionRow(
    Guid Id, string PublicDecisionId, string Subject, string? SubjectName, string Resource,
    string Action, string Result, int? RiskScore, string? RiskLevel, DateTimeOffset EvaluatedAtUtc);

[Authorize]
public sealed class AuthorizationPageController : Controller
{
    private const int PageSize = 25;

    private readonly VeritasDbContext _db;
    private readonly IAuditQueryService _audit;

    public AuthorizationPageController(VeritasDbContext db, IAuditQueryService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index(string? result, int page = 1, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        var query = _db.AuthorizationDecisions.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(result)) query = query.Where(d => d.Result == result);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(d => d.EvaluatedAtUtc)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        var subjectIds = rows.Select(r => r.SubjectUserId).Where(Guid.TryParse).Select(Guid.Parse).Distinct().ToList();
        var subjectNames = subjectIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => subjectIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var resourceIds = rows.Select(r => r.ResourceId).Distinct().ToList();
        var resourceNames = (await _db.Resources.AsNoTracking()
                .Select(r => new { IdText = r.Id.ToString(), r.Name }).ToListAsync(ct))
            .Where(r => resourceIds.Contains(r.IdText))
            .ToDictionary(r => r.IdText, r => r.Name, StringComparer.Ordinal);

        var items = rows.Select(d => new DecisionRow(
            d.Id, $"DEC-{d.Id:N}", d.SubjectUserId,
            Guid.TryParse(d.SubjectUserId, out var sid) && subjectNames.TryGetValue(sid, out var sn) ? sn : null,
            resourceNames.TryGetValue(d.ResourceId, out var rn) ? rn : d.ResourceId,
            d.Action, d.Result, d.RiskScore, d.RiskLevel, d.EvaluatedAtUtc)).ToList();

        return View(new AuthorizationIndexViewModel
        {
            Decisions = items, Total = total, Page = page, Result = result
        });
    }

    /// <summary>The full decision explanation screen (spec section 48).</summary>
    public async Task<IActionResult> Decision(Guid id, CancellationToken ct)
    {
        var explanation = await _audit.GetDecisionAsync(id, ct);
        if (explanation is null) return NotFound();
        return View(explanation);
    }
}
