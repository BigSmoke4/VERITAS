using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Modules.RiskManagement.Domain;

public class RiskEvaluation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid? DecisionId { get; set; }
    public int TotalScore { get; set; }
    public string Level { get; set; } = "LOW";

    /// <summary>JSON array of {signal, points} — the exact additive breakdown, never a random number.</summary>
    public string SignalsJson { get; set; } = "[]";
    public DateTimeOffset EvaluatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Raw observations feeding risk evaluation, so scoring is derived from stored facts, not guesses.</summary>
public class RiskSignalObservation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string SignalType { get; set; } = default!; // e.g. KNOWN_DEVICE, LOGIN_LOCATION, FAILED_LOGIN
    public string Value { get; set; } = default!;
    public DateTimeOffset ObservedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
