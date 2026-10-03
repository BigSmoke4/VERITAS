using Microsoft.EntityFrameworkCore;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.RiskManagement.Application;

public sealed record RiskMatrixCell(int ImpactBand, int LikelihoodBand, int Count);

public sealed record HighRiskUser(Guid UserId, string DisplayName, int LatestScore, string LatestLevel, DateTimeOffset EvaluatedAtUtc);

public sealed record RiskDashboard(
    int Evaluations30d, double AverageScore, int HighRiskEvaluations, int CriticalEvaluations,
    IReadOnlyList<(string Signal, int Occurrences, int TotalPoints)> SignalBreakdown,
    IReadOnlyList<HighRiskUser> TopUsers,
    IReadOnlyList<RiskMatrixCell> Matrix,
    IReadOnlyList<(DateTimeOffset BucketUtc, double AverageScore)> Trend);

public interface IRiskDashboardService
{
    Task<RiskDashboard> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Risk dashboard over real RiskEvaluation rows. The impact/likelihood matrix is
/// derived by bucketing stored scores — a cell is never populated with a number
/// that did not come from an actual evaluation (spec section 51).
/// </summary>
public sealed class RiskDashboardService : IRiskDashboardService
{
    private readonly VeritasDbContext _db;
    public RiskDashboardService(VeritasDbContext db) => _db = db;

    public async Task<RiskDashboard> GetAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);

        var evaluations = await _db.RiskEvaluations.AsNoTracking()
            .Where(e => e.EvaluatedAtUtc >= since)
            .Select(e => new { e.UserId, e.TotalScore, e.Level, e.SignalsJson, e.EvaluatedAtUtc })
            .ToListAsync(ct);

        var signalTally = new Dictionary<string, (int Occurrences, int Points)>(StringComparer.Ordinal);
        foreach (var e in evaluations)
        {
            List<RiskSignalScore>? signals = null;
            try
            {
                signals = System.Text.Json.JsonSerializer.Deserialize<List<RiskSignalScore>>(e.SignalsJson);
            }
            catch (System.Text.Json.JsonException)
            {
                signals = null; // a malformed legacy row must not break the dashboard
            }

            if (signals is null) continue;
            foreach (var s in signals)
            {
                var current = signalTally.TryGetValue(s.Signal, out var v) ? v : (0, 0);
                signalTally[s.Signal] = (current.Item1 + 1, current.Item2 + s.Points);
            }
        }

        var signalBreakdown = signalTally
            .OrderByDescending(kv => kv.Value.Points)
            .Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2))
            .ToList();

        var topUsers = evaluations
            .GroupBy(e => e.UserId)
            .Select(g => g.OrderByDescending(e => e.EvaluatedAtUtc).First())
            .OrderByDescending(e => e.TotalScore)
            .Take(10)
            .ToList();

        var userIds = topUsers.Select(u => u.UserId).Distinct().ToList();
        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        // 3x3 matrix: impact band from score magnitude, likelihood band from how
        // often that user was evaluated in the window.
        var perUser = evaluations.GroupBy(e => e.UserId).ToList();
        var maxFrequency = perUser.Count == 0 ? 1 : perUser.Max(g => g.Count());

        var matrix = new List<RiskMatrixCell>();
        foreach (var group in perUser)
        {
            var peak = group.Max(e => e.TotalScore);
            var impact = peak switch { >= 80 => 2, >= 50 => 1, _ => 0 };
            var frequency = group.Count();
            var likelihood = maxFrequency <= 1 ? 0
                : frequency >= maxFrequency * 2 / 3 ? 2
                : frequency >= maxFrequency / 3 ? 1 : 0;

            var existing = matrix.FirstOrDefault(c => c.ImpactBand == impact && c.LikelihoodBand == likelihood);
            if (existing is null) matrix.Add(new RiskMatrixCell(impact, likelihood, 1));
            else matrix[matrix.IndexOf(existing)] = existing with { Count = existing.Count + 1 };
        }

        var trendStart = DateTimeOffset.UtcNow.AddDays(-14).Date;
        var trend = Enumerable.Range(0, 14).Select(i =>
        {
            var bucket = trendStart.AddDays(i);
            var day = evaluations.Where(e => e.EvaluatedAtUtc.UtcDateTime.Date == bucket).ToList();
            return (bucket, day.Count == 0 ? 0d : Math.Round(day.Average(e => e.TotalScore), 1));
        }).ToList();

        return new RiskDashboard(
            evaluations.Count,
            evaluations.Count == 0 ? 0 : Math.Round(evaluations.Average(e => e.TotalScore), 1),
            evaluations.Count(e => e.Level == "HIGH"),
            evaluations.Count(e => e.Level == "CRITICAL"),
            signalBreakdown,
            topUsers.Select(u => new HighRiskUser(u.UserId,
                names.TryGetValue(u.UserId, out var n) ? n : u.UserId.ToString(),
                u.TotalScore, u.Level, u.EvaluatedAtUtc)).ToList(),
            matrix, trend);
    }
}
