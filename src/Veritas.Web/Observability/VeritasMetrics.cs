using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;

namespace Veritas.Web.Observability;

/// <summary>
/// Central place for the metrics named in spec section 40. Counters are
/// incremented from real code paths (AuthorizationService, PolicyEvaluationEngine
/// callers) — nothing here is a placeholder metric that's never touched.
/// </summary>
public sealed class VeritasMetrics
{
    public const string MeterName = "Veritas.Web";
    public const string ActivitySourceName = "Veritas.Web";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Counter<long> _authorizationRequestsTotal;
    private readonly Counter<long> _authorizationDenialsTotal;
    private readonly Histogram<double> _authorizationLatencyMs;
    private readonly Counter<long> _policyEvaluationsTotal;
    private readonly Counter<long> _accessRequestsTotal;
    private readonly Counter<long> _accessApprovalsTotal;
    private readonly Counter<long> _privilegedAccessTotal;
    private readonly Counter<long> _riskEvaluationsTotal;
    private readonly Counter<long> _auditEventsTotal;
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;

    public VeritasMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _authorizationRequestsTotal = meter.CreateCounter<long>("authorization_requests_total");
        _authorizationDenialsTotal = meter.CreateCounter<long>("authorization_denials_total");
        _authorizationLatencyMs = meter.CreateHistogram<double>("authorization_latency_ms");
        _policyEvaluationsTotal = meter.CreateCounter<long>("policy_evaluations_total");
        _accessRequestsTotal = meter.CreateCounter<long>("access_requests_total");
        _accessApprovalsTotal = meter.CreateCounter<long>("access_approvals_total");
        _privilegedAccessTotal = meter.CreateCounter<long>("privileged_access_total");
        _riskEvaluationsTotal = meter.CreateCounter<long>("risk_evaluations_total");
        _auditEventsTotal = meter.CreateCounter<long>("audit_events_total");
        _cacheHits = meter.CreateCounter<long>("cache_hit_total");
        _cacheMisses = meter.CreateCounter<long>("cache_miss_total");
    }

    public void RecordAuthorizationRequest(bool denied, double latencyMs)
    {
        _authorizationRequestsTotal.Add(1);
        if (denied) _authorizationDenialsTotal.Add(1);
        _authorizationLatencyMs.Record(latencyMs);
    }

    public void RecordPolicyEvaluation() => _policyEvaluationsTotal.Add(1);
    public void RecordAccessRequest() => _accessRequestsTotal.Add(1);
    public void RecordAccessApproval() => _accessApprovalsTotal.Add(1);
    public void RecordPrivilegedAccess() => _privilegedAccessTotal.Add(1);
    public void RecordRiskEvaluation() => _riskEvaluationsTotal.Add(1);
    public void RecordAuditEvent() => _auditEventsTotal.Add(1);
    public void RecordCacheHit() => _cacheHits.Add(1);
    public void RecordCacheMiss() => _cacheMisses.Add(1);

    /// <summary>cache_hit_ratio is computed at scrape time by Prometheus/OTel
    /// collector from cache_hit_total / (cache_hit_total + cache_miss_total)
    /// rather than tracked as a raw gauge here, since ratios shouldn't be
    /// computed and reset independently of their inputs.</summary>
    public const string CacheHitRatioNote = "derived: cache_hit_total / (cache_hit_total + cache_miss_total)";
}
