using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Audit.Presentation.Controllers;

public sealed class AuditListViewModel
{
    public List<Domain.AuditLog> Items { get; init; } = new();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public string? ActionFilter { get; init; }
}

[Authorize]
public sealed class AuditController : Controller
{
    private const int MaxPageSize = 100;
    private readonly VeritasDbContext _db;

    public AuditController(VeritasDbContext db) => _db = db;

    // Server-side paging only — per spec section 30, never load unlimited
    // audit records into the browser.
    public async Task<IActionResult> Index(string? action, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.AuditLogs.AsNoTracking().OrderByDescending(a => a.TimestampUtc).AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return View(new AuditListViewModel
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            ActionFilter = action
        });
    }
}
