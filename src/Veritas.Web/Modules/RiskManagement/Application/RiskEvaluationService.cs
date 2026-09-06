using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.RiskManagement.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.RiskManagement.Application;

public sealed record RiskSignalScore(string Signal, int Points);

public sealed class RiskAssessment
{
    public required int TotalScore { get; init; }
    public required string Level { get; init; }
    public required IReadOnlyList<RiskSignalScore> Breakdown { get; init; }
}

public interface IRiskEvaluationService
{
    Task<RiskAssessment> EvaluateAsync(Guid userId, AuthorizationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Every point on the score is traceable to a real, stored or request-supplied
/// fact — never Random.Next(). Points/thresholds are intentionally simple and
/// centralized here so they're the single place to tune, per spec section 17.
/// </summary>
public sealed class RiskEvaluationService : IRiskEvaluationService
{
    private const int NewDevicePoints = 20;
    private const int UnusualLocationPoints = 15;
    private const int PrivilegedActionPoints = 25;
    private const int ProductionResourcePoints = 12;
    private const int OutsideBusinessHoursPoints = 10;
    private const int RecentFailedLoginsPoints = 15;
    private const int LowDeviceTrustPoints = 15;

    private static readonly HashSet<string> PrivilegedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete", "approve", "grant", "revoke", "publish"
    };

    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;

    public RiskEvaluationService(VeritasDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<RiskAssessment> EvaluateAsync(Guid userId, AuthorizationRequest request, CancellationToken ct = default)
    {
        var scores = new List<RiskSignalScore>();

        var knownDevice = request.Ip is null
            ? false
            : await _db.Set<RiskSignalObservation>()
                .AnyAsync(s => s.UserId == userId && s.SignalType == "KNOWN_IP" && s.Value == request.Ip, ct);
        if (!knownDevice)
            scores.Add(new RiskSignalScore("New device / unrecognized IP", NewDevicePoints));

        var recentFailedLogins = await _db.Set<RiskSignalObservation>()
            .CountAsync(s => s.UserId == userId
                           && s.SignalType == "FAILED_LOGIN"
                           && s.ObservedAtUtc >= DateTimeOffset.UtcNow.AddHours(-1), ct);
        if (recentFailedLogins >= 3)
            scores.Add(new RiskSignalScore($"{recentFailedLogins} failed logins in the last hour", RecentFailedLoginsPoints));

        if (PrivilegedActions.Contains(request.Action))
            scores.Add(new RiskSignalScore($"Privileged action requested ({request.Action})", PrivilegedActionPoints));

        if (string.Equals(request.Environment, "production", StringComparison.OrdinalIgnoreCase))
            scores.Add(new RiskSignalScore("Target environment is production", ProductionResourcePoints));

        var nowUtc = DateTimeOffset.UtcNow;
        var isOutsideBusinessHours = nowUtc.Hour is < 7 or >= 19;
        if (isOutsideBusinessHours)
            scores.Add(new RiskSignalScore("Request made outside business hours (UTC)", OutsideBusinessHoursPoints));

        if (string.Equals(request.DeviceTrust, "LOW", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(request.DeviceTrust))
            scores.Add(new RiskSignalScore("Device trust is low or unknown", LowDeviceTrustPoints));

        var lastKnownLocation = await _db.Set<RiskSignalObservation>()
            .Where(s => s.UserId == userId && s.SignalType == "LOGIN_LOCATION")
            .OrderByDescending(s => s.ObservedAtUtc)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (lastKnownLocation is not null && request.Ip is not null && lastKnownLocation != request.Ip)
            scores.Add(new RiskSignalScore("Location differs from last known login location", UnusualLocationPoints));

        var total = Math.Min(scores.Sum(s => s.Points), 100);
        var level = total switch
        {
            < 30 => "LOW",
            < 60 => "MEDIUM",
            < 80 => "HIGH",
            _ => "CRITICAL"
        };

        _db.Set<RiskEvaluation>().Add(new RiskEvaluation
        {
            OrganizationId = _tenant.OrganizationId,
            UserId = userId,
            TotalScore = total,
            Level = level,
            SignalsJson = System.Text.Json.JsonSerializer.Serialize(scores)
        });
        await _db.SaveChangesAsync(ct);

        return new RiskAssessment { TotalScore = total, Level = level, Breakdown = scores };
    }
}
