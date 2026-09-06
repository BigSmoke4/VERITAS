# ADR-005: Deny-by-Default (Zero Trust) Evaluation

## Status
Accepted

## Decision
`PolicyEvaluationEngine.Evaluate` returns DENY whenever no published policy
version applies, or none of the applicable rules' conditions fully match.
There is no implicit allow path anywhere in the evaluator.

## Consequences
- A misconfigured/unpublished policy fails safe (denies), not open.
- This means "policy accidentally locks out an entire organization" is a
  real risk in the other direction — mitigated by the Policy Simulator
  (planned), which must be run against real current users/resources before
  any publish.
