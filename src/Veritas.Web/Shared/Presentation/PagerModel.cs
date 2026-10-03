namespace Veritas.Web.Shared.Presentation;

/// <summary>
/// View model for the shared pager partial. <see cref="UrlFor"/> is supplied by
/// the calling view so the pager stays reusable across every explorer without
/// knowing their route shapes.
/// </summary>
public sealed class PagerModel
{
    public required int Total { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required Func<int, string?> UrlFor { get; init; }
}
