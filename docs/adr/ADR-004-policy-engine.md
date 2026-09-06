# ADR-004: Structured, Data-Driven Policy Rules — No Embedded Code Execution

## Status
Accepted

## Decision
Policies are rows (PolicyRule, PolicyCondition), not code. The evaluator
(`PolicyEvaluationEngine`) interprets Attribute/Operator/Value triples; it
never calls `eval`, `Roslyn.Compile`, reflection-invoked user strings, or
anything else that would let a policy author run arbitrary code inside the
authorization hot path.

## Consequences
- Expressiveness is bounded by the operator set (Equals, NotEquals,
  LessThanOrEqual, GreaterThanOrEqual, In). New operators are a deliberate,
  reviewed code change — not something a policy author can smuggle in.
- Because rules are pure data, the exact same evaluator powers both the live
  `/api/v1/authorize` endpoint and the Policy Simulator (planned) — the
  simulator is not a separate, potentially-diverging implementation.
