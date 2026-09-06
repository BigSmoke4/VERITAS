using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Modules.RiskManagement.Application;
using Veritas.Web.Infrastructure.Caching;
using Veritas.Web.Observability;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Authorization.Application;

public interface IAuthorizationService
{
    Task<AuthorizationDecisionOutcome> AuthorizeAsync(AuthorizationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates a single authorize call: build real attributes -> evaluate
/// policy -> persist the decision (for reproducibility) -> write an audit
/// event. This is the class the /api/v1/authorize controller delegates to;
/// it contains no business logic of its own beyond wiring, per section 3
/// (thin controllers, but also thin/no-logic orchestration layers — the
/// actual decision logic lives in PolicyEvaluationEngine, which is pure and
/// independently unit tested).
/// </summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private readonly VeritasDbContext _db;
    private readonly IPolicyEvaluationEngine _engine;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;
    private readonly IRiskEvaluationService _risk;
    private readonly IPolicyCache _policyCache;
    private readonly VeritasMetrics _metrics;

    public AuthorizationService(
        VeritasDbContext db,
        IPolicyEvaluationEngine engine,
        ITenantContext tenant,
        IAuditService audit,
        IRiskEvaluationService risk,
        IPolicyCache policyCache,
        VeritasMetrics metrics)
    {
        _db = db;
        _engine = engine;
        _tenant = tenant;
        _audit = audit;
        _risk = risk;
        _policyCache = policyCache;
        _metrics = metrics;
    }

    public async Task<AuthorizationDecisionOutcome> AuthorizeAsync(AuthorizationRequest request, CancellationToken ct = default)
    {
        using var activity = VeritasMetrics.ActivitySource.StartActivity("authorize");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await AuthorizeInternalAsync(request, ct);
        stopwatch.Stop();

        activity?.SetTag("veritas.decision", outcome.Result.ToString());
        activity?.SetTag("veritas.decision_id", outcome.DecisionId);
        _metrics.RecordAuthorizationRequest(
            denied: outcome.Result == AuthorizationDecisionResult.Deny,
            latencyMs: stopwatch.Elapsed.TotalMilliseconds);
        _metrics.RecordPolicyEvaluation();
        _metrics.RecordAuditEvent();

        return outcome;
    }

    private async Task<AuthorizationDecisionOutcome> AuthorizeInternalAsync(AuthorizationRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.SubjectUserId, out var userId) ||
            !Guid.TryParse(request.ResourceId, out var resourceId))
        {
            return Deny("Subject or resource id was not a valid identifier.");
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        var resource = await _db.Resources.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == resourceId, ct);

        if (user is null) return Deny("Subject not found or not in this tenant.");
        if (resource is null) return Deny("Resource not found or not in this tenant.");
        if (user.LifecycleState != Identity.Domain.UserLifecycleState.Active)
            return Deny($"User lifecycle state is {user.LifecycleState}, not ACTIVE.");

        var attributes = new AttributeBag();
        attributes.Set("user.id", user.Id.ToString());
        attributes.Set("user.status", user.LifecycleState);
        attributes.Set("user.riskLevel", user.RiskLevel);
        attributes.Set("resource.id", resource.Id.ToString());
        attributes.Set("resource.classification", resource.Classification);
        attributes.Set("resource.department", resource.OwnerDepartment);
        attributes.Set("resource.environment", resource.Environment);
        attributes.Set("request.action", request.Action);
        attributes.Set("request.environment", request.Environment);
        attributes.Set("request.ip", request.Ip);
        attributes.Set("request.deviceTrust", request.DeviceTrust ?? "UNKNOWN");
        attributes.Set("request.authenticationStrength", request.AuthenticationStrength ?? "STANDARD");

        var risk = await _risk.EvaluateAsync(userId, request, ct);
        _metrics.RecordRiskEvaluation();
        attributes.Set("request.riskLevel", risk.Level);
        attributes.Set("request.riskScore", risk.TotalScore.ToString());

        // Published policy versions are read through the Redis-backed cache
        // (RedisPolicyCache) — falls back to direct Postgres reads if Redis is
        // down, and is invalidated whenever a new version is published.
        var candidateVersions = await _policyCache.GetPublishedVersionsAsync(_tenant.OrganizationId, ct);

        var policyOutcome = _engine.Evaluate(candidateVersions, attributes);
        var outcome = ApplyRiskGate(policyOutcome, risk);

        _db.AuthorizationDecisions.Add(new AuthorizationDecisionRecord
        {
            Id = outcome.DecisionId,
            OrganizationId = _tenant.OrganizationId,
            SubjectUserId = request.SubjectUserId,
            ResourceId = request.ResourceId,
            Action = request.Action,
            Result = outcome.Result.ToString(),
            PolicyId = outcome.MatchedRule?.PolicyId,
            PolicyVersionId = outcome.MatchedRule?.PolicyVersionId,
            ReasonsJson = System.Text.Json.JsonSerializer.Serialize(outcome.Reasons),
            ExpiresAtUtc = outcome.ExpiresAtUtc
        });
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: outcome.Result == AuthorizationDecisionResult.Allow ? "AUTHORIZATION_ALLOWED" : "AUTHORIZATION_DENIED",
            resourceId: request.ResourceId,
            previousValue: null,
            newValue: outcome.Result.ToString(),
            decisionId: outcome.DecisionId,
            correlationId: request.IdempotencyKey,
            ct: ct);

        return outcome;
    }

    /// <summary>
    /// Risk-based authorization per spec section 18: risk can only escalate a
    /// policy's verdict toward stricter (never loosen a policy DENY into an
    /// ALLOW). Thresholds: &lt;30 no change, 30-60 no change to result but
    /// flagged, 60-80 escalate ALLOW to REQUIRE_APPROVAL, &gt;80 escalate to DENY.
    /// </summary>
    private static AuthorizationDecisionOutcome ApplyRiskGate(AuthorizationDecisionOutcome policyOutcome, RiskManagement.Application.RiskAssessment risk)
    {
        var reasons = policyOutcome.Reasons.ToList();
        var result = policyOutcome.Result;

        if (policyOutcome.Result == AuthorizationDecisionResult.Allow)
        {
            if (risk.TotalScore > 80)
            {
                result = AuthorizationDecisionResult.Deny;
                reasons.Add($"Risk gate: score {risk.TotalScore} (>80) overrides policy ALLOW with DENY.");
            }
            else if (risk.TotalScore > 60)
            {
                result = AuthorizationDecisionResult.RequireApproval;
                reasons.Add($"Risk gate: score {risk.TotalScore} (60-80) escalates ALLOW to REQUIRE_APPROVAL.");
            }
            else if (risk.TotalScore > 30)
            {
                reasons.Add($"Risk gate: score {risk.TotalScore} (30-60) — ALLOW retained, additional verification recommended.");
            }
        }

        return new AuthorizationDecisionOutcome
        {
            Result = result,
            Reasons = reasons,
            MatchedRule = policyOutcome.MatchedRule,
            RiskScore = risk.TotalScore,
            ExpiresAtUtc = policyOutcome.ExpiresAtUtc
        };
    }

    private static AuthorizationDecisionOutcome Deny(string reason) => new()
    {
        Result = AuthorizationDecisionResult.Deny,
        Reasons = new[] { reason }
    };
}
