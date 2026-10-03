# Compliance and security-event detection

## Controls

A `CompliancePolicy` is a named control with a severity, a numeric threshold and an
optional framework reference (for example `SOC2 CC6.1`, `ISO 27001 A.9.2`). The demo
tenant seeds six.

## Findings

`ComplianceService.ScanAsync` evaluates every enabled control against real rows and
produces `ComplianceFinding` records. Each finding carries a stable dedupe key, so a
re-scan updates the existing finding's `LastSeenAtUtc` instead of creating a duplicate:

| Dedupe key | Control |
| --- | --- |
| `inactive-user:{userId}` | account has not authenticated within the threshold |
| `dormant-privileged:{userId}` | privileged account dormant beyond the threshold |
| `unused-permission:{id}` | permission granted by no role and held by nobody |
| `orphaned-account:{userId}` | account with no department |
| `excessive-privileges:{userId}` | role count above the threshold |
| `expired-grant:{grantId}` | temporary grant left past its window |

**Findings that stop reproducing are auto-resolved.** A scan that no longer detects a
condition marks the matching open finding `Resolved` rather than leaving it open forever,
because a compliance queue that only grows is a compliance queue nobody reads.

## Security-event detection

`SecurityDetectionWorker` looks for behavioural patterns in the audit stream. Each
detector has its own dedupe key and window, so a sustained condition raises one event per
window rather than one per matching row.

| Detector | Condition | Dedupe key |
| --- | --- | --- |
| `REPEATED_DENIALS` | ≥ 5 denials by one actor in 10 minutes | `repeated-denials:{actor}:{bucket}` |
| `RAPID_PRIVILEGE_ESCALATION` | ≥ 3 `ROLE_ASSIGNED` events in 10 minutes | per actor + window |
| `UNUSUAL_PRIVILEGED_ACCESS` | ≥ 3 `PRIVILEGED_ACCESS_GRANTED` in 10 minutes | per actor + window |
| `ACCESS_REQUEST_BURST` | ≥ 4 access requests in 10 minutes | per actor + window |
| `ACCESS_REVIEW_OVERDUE` | an access review past its due date | per campaign |
| `CRITICAL_RISK_USER` | a subject reaching `CRITICAL` | `critical-risk:{userId}:{yyyyMMddHH}` |
| `SEPARATION_OF_DUTIES_VIOLATION` | an SoD breach was attempted | per rule + subject |

Events carry a severity and a free-text detail, and can be acknowledged from the
Compliance screen.
