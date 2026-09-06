using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Authorization.Presentation;

public sealed class AccessGraphViewModel
{
    public List<(Guid Id, string Label)> Users { get; init; } = new();
    public List<(Guid Id, string Label)> Resources { get; init; } = new();
    public AccessGraphDto? Graph { get; init; }
    public string? Mode { get; init; }
    public Guid? SelectedId { get; init; }
}

/// <summary>
/// Razor UI for the Access Graph. Uses the exact same IAccessGraphQueryService
/// as the JSON API (AccessGraphController) — the graph rendered here and the
/// graph returned by curl/Swagger are guaranteed identical because they come
/// from the same query, not two implementations.
/// </summary>
[Authorize]
public sealed class AccessGraphPageController : Controller
{
    private readonly VeritasDbContext _db;
    private readonly IAccessGraphQueryService _graph;

    public AccessGraphPageController(VeritasDbContext db, IAccessGraphQueryService graph)
    {
        _db = db;
        _graph = graph;
    }

    public async Task<IActionResult> Index(string? mode, Guid? id, CancellationToken ct)
    {
        var users = (await _db.Users.ToListAsync(ct)).Select(u => (u.Id, u.DisplayName ?? u.UserName ?? u.Id.ToString())).ToList();
        var resources = (await _db.Resources.ToListAsync(ct)).Select(r => (r.Id, r.Name)).ToList();

        AccessGraphDto? graph = null;
        if (id is not null && !string.IsNullOrEmpty(mode))
        {
            graph = mode == "resource"
                ? await _graph.BuildForResourceAsync(id.Value, ct)
                : await _graph.BuildForUserAsync(id.Value, ct);
        }

        return View("~/Views/AccessGraph/Index.cshtml", new AccessGraphViewModel
        {
            Users = users,
            Resources = resources,
            Mode = mode,
            SelectedId = id,
            Graph = graph
        });
    }
}
