using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.AccessReview.Application;
using Veritas.Web.Modules.AccessReview.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.AccessReview.Presentation;

public sealed class AccessReviewsViewModel
{
    public List<AccessReviewCampaign> Campaigns { get; init; } = new();
    public Dictionary<Guid, ReviewProgress> Progress { get; init; } = new();
    public AccessReviewCampaign? SelectedCampaign { get; init; }
    public List<AccessReviewItem> SelectedItems { get; init; } = new();
    public List<(Guid Id, string Label)> Users { get; init; } = new();
}

[Authorize]
public sealed class AccessReviewsPageController : Controller
{
    private readonly VeritasDbContext _db;
    private readonly IAccessReviewService _reviews;

    public AccessReviewsPageController(VeritasDbContext db, IAccessReviewService reviews)
    {
        _db = db;
        _reviews = reviews;
    }

    public async Task<IActionResult> Index(Guid? campaignId, CancellationToken ct)
    {
        var campaigns = await _db.AccessReviewCampaigns.ToListAsync(ct);
        var progress = new Dictionary<Guid, ReviewProgress>();
        foreach (var c in campaigns)
            progress[c.Id] = await _reviews.GetProgressAsync(c.Id, ct);

        var vm = new AccessReviewsViewModel { Campaigns = campaigns, Progress = progress };

        if (campaignId is not null)
        {
            vm = new AccessReviewsViewModel
            {
                Campaigns = campaigns,
                Progress = progress,
                SelectedCampaign = campaigns.FirstOrDefault(c => c.Id == campaignId),
                SelectedItems = await _db.AccessReviewItems.Where(i => i.AccessReviewCampaignId == campaignId).ToListAsync(ct)
            };
        }

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCampaign(string name, Guid reviewerUserId, int dueInDays, CancellationToken ct)
    {
        var campaign = await _reviews.CreateCampaignFromCurrentGrantsAsync(
            name, reviewerUserId, DateTimeOffset.UtcNow.AddDays(dueInDays), ct);
        return RedirectToAction(nameof(Index), new { campaignId = campaign.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(Guid itemId, Guid campaignId, AccessReviewDecisionType decision, string? note, CancellationToken ct)
    {
        await _reviews.RecordDecisionAsync(itemId, decision, note, null, ct);
        return RedirectToAction(nameof(Index), new { campaignId });
    }
}
