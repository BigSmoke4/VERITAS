# Risk engine

Risk scoring exists to make risky decisions visible and to tighten authorization when
conditions warrant it. It never grants anything.

## Signals

`RiskEvaluationService.EvaluateAsync` produces a `RiskAssessment`:

```csharp
record RiskAssessment(int TotalScore, string Level, IReadOnlyList<RiskSignalScore> Breakdown);
record RiskSignalScore(string Signal, int Points);
```

Every point in the total is attributable to a named signal in `Breakdown`. The UI renders
that breakdown rather than a bare number, because "score 74" is not actionable and
"score 74, of which +30 off-hours, +25 first access to this resource, +19 elevated
clearance gap" is.

`SignalsJson` is persisted with each `RiskEvaluation`, so a historical score can be
explained months later without re-running the engine against data that has since changed.

## Bands

| Score | Level |
| --- | --- |
| ≥ 80 | `CRITICAL` |
| ≥ 60 | `HIGH` |
| ≥ 30 | `MEDIUM` |
| < 30 | `LOW` |

`CK_RiskEvaluation_Score` constrains stored scores to 0…100.

## How risk affects a decision

`ApplyRiskGate` can only tighten. A policy `Allow` may be downgraded by risk; a policy
`Deny` is never upgraded. Risk is a constraint on entitlement, never a source of it.

## The dashboard

`RiskDashboardService` derives every figure from stored `RiskEvaluation` rows:

- **Average score, high-risk and critical counts** over the last 30 days.
- **Signal contribution** — which signals fired, how often, and how many points they
  contributed in total.
- **Impact / likelihood matrix** — impact is bucketed from each subject's *peak* score
  (≥80 / ≥50 / lower); likelihood is bucketed by evaluation frequency, split into thirds
  across the observed range. A cell is never populated with a number that did not come
  from an actual evaluation.

If no evaluations exist in the window, the dashboard says so. It does not render zeroes
that would imply measurements were taken.
