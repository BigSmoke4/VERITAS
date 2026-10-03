using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Veritas.Web.Controllers;

/// <summary>
/// Terminal error page for the MVC surface (spec section 39). Deliberately
/// exposes only a correlation id and a generic message — never a stack trace,
/// an exception type, a SQL fragment, or a filesystem path. The full detail is
/// already in the structured log, keyed by the same correlation id.
/// </summary>
public sealed class HomeController : Controller
{
    public const string CorrelationIdItemKey = "Veritas.CorrelationId";

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var correlationId = HttpContext.Items[CorrelationIdItemKey] as string
                            ?? Activity.Current?.TraceId.ToString()
                            ?? Guid.NewGuid().ToString("N");

        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View(new ErrorViewModel { RequestId = correlationId });
    }
}

public sealed class ErrorViewModel
{
    public string RequestId { get; init; } = string.Empty;
}
