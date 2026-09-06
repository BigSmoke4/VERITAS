# ADR-006: Every Authorization Decision Is Persisted With Its Exact Policy Version

## Status
Accepted

## Decision
`AuthorizationDecisionRecord` stores PolicyId + PolicyVersionId (not just
PolicyId) alongside the decision. Policy versions are immutable once
Published (enforced at the application-service layer that transitions
lifecycle state, not yet at the DB constraint layer — see docs/security.md
for the current gap).

## Consequences
Reproducing "why was this decision made six months ago" means: look up the
AuthorizationDecisionRecord, load that exact PolicyVersion (still in the DB,
untouched even though a newer version may since have published), and re-run
`PolicyEvaluationEngine.Evaluate` against the same recorded attribute
inputs — which is why ReasonsJson stores the evaluated condition strings,
not just the final verdict.
