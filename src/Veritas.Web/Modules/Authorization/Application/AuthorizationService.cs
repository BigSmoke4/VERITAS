using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Veritas.Web.Infrastructure.Caching;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Modules.RiskManagement.Application;
using Veritas.Web.Modules.RoleManagement.Application;
using Veritas.Web.Observability;
using Veritas.Web.Shared.Application.AccessGrants;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Authorization.Application;

public interface IAuthorizationService
{
    Task<AuthorizationDecisionOutcome> AuthorizeAsync(AuthorizationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates one authorize call through the full Zero Trust pipeline
/// (spec section 16): Identity -> Context -> Policy -> Risk -> Decision.
///
/// Nothing here is decorative. Each stage appends a <see cref="DecisionCheck"/>
/// so the returned explanation is a transcript of what was actually executed,
/// and the persisted AuthorizationDecisionRecord names the exact
/// PolicyVersionId used, which is what makes a six-month-old decision
/// reproducible (ADR-006).
/// </summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private readonly VeritasDbContext _db;
    private readonly IPolicyEvaluationEngine _engine;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;
    private readonly IRiskEvaluationService _risk;
    private readonly IRbacEvaluator _rbac;
    private readonly ITemporaryGrantReader _grants;
    private readonly IPolicyCache _policyCache;
    private readonly VeritasMetrics _metrics;
    private readonly SecurityOptions _security;

    public AuthorizationService(
        VeritasDbContext db,
        IPolicyEvaluationEngine engine,
        ITenantContext tenant,
        IAuditService audit,
        IRiskEvaluationService risk,
        IRbacEvaluator rbac,
        ITemporaryGrantReader grants,
        IPolicyCache policyCache,
        VeritasMetrics metrics,
        IOptions<SecurityOptions> security)
    {
        _db = db;
        _engine = engine;
        _tenant = tenant;
        _audit = audit;
        _risk = risk;
        _rbac = rbac;
        _grants = grants;
        _policyCache = policyCache;
        _metrics = metrics;
        _security = security.Value;
    }

    public async Task<AuthorizationDecisionOutcome> AuthorizeAsync(AuthorizationRequest request, CancellationToken ct = default)
    {
        using var activity = VeritasMetrics.ActivitySource.StartActivity("authorize");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await AuthorizeInternalAsync(request, ct);

        stopwatch.Stop();
        activity?.SetTag("veritas.decision", outcome.Result.ToString());
        activity?.SetTag("veritas.decision_id", outcome.DecisionId);
        activity?.SetTag("veritas.risk_score", outcome.RiskScore);

        _metrics.RecordAuthorizationRequest(
            denied: outcome.Result == AuthorizationDecisionResult.Deny,
            latencyMs: stopwatch.Elapsed.TotalMilliseconds);
        _metrics.RecordPolicyEvaluation();
        _metrics.RecordAuditEvent();

        return outcome;
    }

    private async Task<AuthorizationDecisionOutcome> AuthorizeInternalAsync(AuthorizationRequest request, CancellationToken ct)
    {
        var checks = new List<DecisionCheck>();
        var reasons = new List<string>();

        // --- 1. Identity -------------------------------------------------------
        var identityOk = Guid.TryParse(request.SubjectUserId, out var userId);
        checks.Add(new DecisionCheck("IDENTITY_VERIFIED", "Subject identifier is well-formed", identityOk,
            identityOk ? request.SubjectUserId : $"'{request.SubjectUserId}' is not a valid identifier"));
        if (!identityOk)
            return await FinalizeAsync(Denial(reasons, checks, "Subject identifier is not valid."), request, null, ct);

        var tenantOk = _tenant.IsResolved;
        checks.Add(new DecisionCheck("TENANT_VERIFIED", "Request is bound to a resolved tenant", tenantOk,
            tenantOk ? _tenant.OrganizationId.ToString() : "No org_id claim on the calling principal"));
        if (!tenantOk)
            return await FinalizeAsync(Denial(reasons, checks, "Tenant could not be resolved for this principal."), request, null, ct);

        // --- 2. Context (subject + resource, loaded from real rows) -------------
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        checks.Add(new DecisionCheck("SUBJECT_EXISTS", "Subject exists in this tenant", user is not null,
            user is null ? "No such user inside the caller's tenant" : user.DisplayName));
        if (user is null)
            return await FinalizeAsync(Denial(reasons, checks, "Subject not found in this tenant."), request, null, ct);

        var department = user.DepartmentId is null
            ? null
            : await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == user.DepartmentId, ct);

        var accountActive = user.LifecycleState == Identity.Domain.UserLifecycleState.Active;
        checks.Add(new DecisionCheck("ACCOUNT_ACTIVE", "Subject account is in the ACTIVE lifecycle state", accountActive,
            $"Lifecycle state is {user.LifecycleState}"));
        if (!accountActive)
            return await FinalizeAsync(
                Denial(reasons, checks, $"User lifecycle state is {user.LifecycleState}; only ACTIVE subjects may be authorized."),
                request, null, ct);

        var resourceOk = Guid.TryParse(request.ResourceId, out var resourceId);
        var resource = resourceOk
            ? await _db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId, ct)
            : null;
        checks.Add(new DecisionCheck("RESOURCE_EXISTS", "Resource exists in this tenant", resource is not null,
            resource is null ? "No such resource inside the caller's tenant" : resource.Name));
        if (resource is null)
            return await FinalizeAsync(Denial(reasons, checks, "Resource not found in this tenant."), request, null, ct);

        var application = await _db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == resource!.ApplicationId, ct);

        // --- 3. RBAC: the subject must actually hold the required permission ----
        var requiredPermission = BuildPermissionKey(resource, request.Action);
        var rbac = await _rbac.CheckAsync(userId, requiredPermission, ct);

        if (!rbac.Satisfied)
        {
            // Distinguish "never had it" from "had it and it lapsed" — the second
            // is the answer the JIT demo scenario (section 73, step 11) needs.
            var lastGrant = await _grants.GetMostRecentGrantAsync(userId, resource.Id, requiredPermission, ct);
            if (lastGrant is not null)
            {
                var lapseDetail = lastGrant.Revoked
                    ? $"Temporary grant {lastGrant.GrantId:N} was revoked at {lastGrant.RevokedAtUtc:O} ({lastGrant.RevokedReason})."
                    : $"Temporary grant {lastGrant.GrantId:N} expired at {lastGrant.ExpiresAtUtc:O}.";

                checks.Add(new DecisionCheck("PERMISSION_HELD", $"Subject holds '{requiredPermission}'", false, lapseDetail));
                reasons.Add("Temporary access expired.");
                reasons.Add(lapseDetail);
            }
            else
            {
                checks.Add(new DecisionCheck("PERMISSION_HELD", $"Subject holds '{requiredPermission}'", false, rbac.Explanation));
                reasons.Add($"Required permission missing: {requiredPermission}.");
            }

            return await FinalizeAsync(
                new AuthorizationDecisionOutcome
                {
                    Result = AuthorizationDecisionResult.Deny,
                    Reasons = reasons,
                    Checks = checks,
                    RequiredPermissionKey = requiredPermission
                }, request, requiredPermission, ct);
        }

        checks.Add(new DecisionCheck("PERMISSION_HELD", $"Subject holds '{requiredPermission}'", true, rbac.Explanation));

        // --- 4. Attribute bag built from real column values ---------------------
        var attributes = new AttributeBag();
        attributes.Set("user.id", user.Id.ToString());
        attributes.Set("user.name", user.DisplayName);
        attributes.Set("user.status", user.LifecycleState);
        attributes.Set("user.riskLevel", user.RiskLevel);
        attributes.Set("user.clearance", user.Clearance);
        attributes.Set("user.department", department?.Name);
        attributes.Set("user.roles", string.Join(",", (await _rbac.ResolveAsync(userId, ct)).RoleNames));
        attributes.Set("resource.id", resource.Id.ToString());
        attributes.Set("resource.name", resource.Name);
        attributes.Set("resource.type", resource.ResourceType);
        attributes.Set("resource.classification", resource.Classification);
        attributes.Set("resource.department", resource.OwnerDepartment);
        attributes.Set("resource.environment", resource.Environment);
        attributes.Set("resource.owner", resource.OwnerDepartment);
        attributes.Set("application.name", application?.Name);
        attributes.Set("application.environment", application?.Environment);
        attributes.Set("request.action", request.Action);
        attributes.Set("request.environment", request.Environment);
        attributes.Set("request.ip", request.Ip);
        attributes.Set("request.time", DateTimeOffset.UtcNow.ToString("O"));
        attributes.Set("request.deviceTrust", string.IsNullOrWhiteSpace(request.DeviceTrust) ? "UNKNOWN" : request.DeviceTrust);
        attributes.Set("request.authenticationStrength",
            string.IsNullOrWhiteSpace(request.AuthenticationStrength) ? "STANDARD" : request.AuthenticationStrength);

        // --- 5. Risk (explainable, additive — see RiskEvaluationService) --------
        var risk = await _risk.EvaluateAsync(userId, request, ct);
        _metrics.RecordRiskEvaluation();
        attributes.Set("request.riskLevel", risk.Level);
        attributes.Set("request.riskScore", risk.TotalScore.ToString());

        // --- 6. Policy evaluation against the published version set -------------
        var candidateVersions = await _policyCache.GetPublishedVersionsAsync(_tenant.OrganizationId, ct);
        checks.Add(new DecisionCheck("POLICY_LOADED", "Published policy set loaded", candidateVersions.Count > 0,
            $"{candidateVersions.Count} published policy version(s) in scope"));

        var policyOutcome = _engine.Evaluate(candidateVersions, attributes);
        var policyName = policyOutcome.MatchedRule is null
            ? null
            : await _db.Policies.AsNoTracking()
                .Where(p => p.Id == policyOutcome.MatchedRule.PolicyId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct);

        // --- 7. Risk gate: can only tighten a policy verdict, never loosen it ---
        var (result, riskNotes) = ApplyRiskGate(policyOutcome.Result, risk);
        reasons.AddRange(policyOutcome.Reasons);
        reasons.AddRange(riskNotes);

        var mergedChecks = checks.Concat(policyOutcome.Checks).ToList();
        mergedChecks.Add(new DecisionCheck(
            "RISK_WITHIN_TOLERANCE",
            $"Risk score {risk.TotalScore} is within the threshold for {policyOutcome.Result}",
            result == policyOutcome.Result || result != AuthorizationDecisionResult.Deny,
            $"{risk.Level}; approval threshold {_security.RiskApprovalThreshold}, deny threshold {_security.RiskDenyThreshold}"));

        var expiresAt = policyOutcome.ExpiresAtUtc
            ?? (rbac.ExpiresAtUtc is not null && policyOutcome.Result == AuthorizationDecisionResult.Allow
                ? rbac.ExpiresAtUtc
                : null);

        var outcome = new AuthorizationDecisionOutcome
        {
            Result = result,
            Reasons = reasons,
            Checks = mergedChecks,
            MatchedRule = policyOutcome.MatchedRule,
            PolicyName = policyName,
            RiskScore = risk.TotalScore,
            RiskLevel = risk.Level,
            RequiredPermissionKey = requiredPermission,
            ExpiresAtUtc = expiresAt
        };

        return await FinalizeAsync(outcome, request, requiredPermission, ct);
    }

    /// <summary>
    /// Persists the decision and writes the audit event. Kept in one place so
    /// there is exactly one code path that can produce an audited decision —
    /// including the early DENY returns above.
    /// </summary>
    private async Task<AuthorizationDecisionOutcome> FinalizeAsync(
        AuthorizationDecisionOutcome outcome, AuthorizationRequest request, string? permissionKey, CancellationToken ct)
    {
        _db.AuthorizationDecisions.Add(new AuthorizationDecisionRecord
        {
            Id = outcome.DecisionId,
            OrganizationId = _tenant.OrganizationId,
            SubjectUserId = request.SubjectUserId,
            ResourceId = request.ResourceId,
            Action = request.Action,
            Environment = request.Environment,
            Result = outcome.Result.ToString(),
            PolicyId = outcome.MatchedRule?.PolicyId,
            PolicyVersionId = outcome.MatchedRule?.PolicyVersionId,
            RiskScore = outcome.RiskScore,
            RiskLevel = outcome.RiskLevel,
            RequiredPermissionKey = permissionKey,
            ReasonsJson = System.Text.Json.JsonSerializer.Serialize(outcome.Reasons),
            ChecksJson = System.Text.Json.JsonSerializer.Serialize(outcome.Checks),
            ContextJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                request.Ip,
                request.DeviceTrust,
                request.AuthenticationStrength,
                request.IdempotencyKey
            }),
            CorrelationId = request.IdempotencyKey,
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
    /// Risk-based authorization (spec section 18). Risk may only escalate a
    /// policy verdict toward stricter: a policy DENY is never softened, and a
    /// policy REQUIRE_APPROVAL is never turned into an ALLOW.
    /// Thresholds come from <see cref="SecurityOptions"/> so they are
    /// configurable without a redeploy of code.
    /// </summary>
    private (AuthorizationDecisionResult Result, List<string> Notes) ApplyRiskGate(
        AuthorizationDecisionResult policyResult, RiskAssessment risk)
    {
        var notes = new List<string>();
        if (policyResult != AuthorizationDecisionResult.Allow)
            return (policyResult, notes);

        if (risk.TotalScore >= _security.RiskDenyThreshold)
        {
            notes.Add($"Risk gate: score {risk.TotalScore} >= {_security.RiskDenyThreshold} overrides policy ALLOW with DENY.");
            return (AuthorizationDecisionResult.Deny, notes);
        }

        if (risk.TotalScore >= _security.RiskApprovalThreshold)
        {
            notes.Add($"Risk gate: score {risk.TotalScore} >= {_security.RiskApprovalThreshold} escalates ALLOW to REQUIRE_APPROVAL.");
            return (AuthorizationDecisionResult.RequireApproval, notes);
        }

        if (risk.TotalScore >= _security.RiskVerificationThreshold)
        {
            notes.Add($"Risk gate: score {risk.TotalScore} >= {_security.RiskVerificationThreshold} — ALLOW retained, step-up verification recommended.");
        }

        return (policyResult, notes);
    }

    /// <summary>
    /// Permission keys follow the <c>resource.action</c> convention from spec
    /// section 9, derived from the resource's own stored prefix so the mapping
    /// is data, not a hardcoded switch.
    /// </summary>
    private static string BuildPermissionKey(ResourceManagement.Domain.Resource resource, string action) =>
        $"{resource.PermissionKeyPrefix.Trim().ToLowerInvariant()}.{action.Trim().ToLowerInvariant()}";

    private static AuthorizationDecisionOutcome Denial(
        List<string> reasons, List<DecisionCheck> checks, string reason)
    {
        reasons.Add(reason);
        return new AuthorizationDecisionOutcome
        {
            Result = AuthorizationDecisionResult.Deny,
            Reasons = reasons,
            Checks = checks
        };
    }
}
