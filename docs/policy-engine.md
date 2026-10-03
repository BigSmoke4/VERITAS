# Policy engine

`PolicyEvaluationEngine` is pure: it takes a set of published `PolicyVersion`s and an
`AttributeBag`, and returns a verdict. It has no database access, no clock, and no
ambient state, which is what makes it unit-testable and what makes the simulator honest.

## Rule structure

A policy version is an ordered list of rules. Each rule is a priority, an effect, and a
list of structured conditions.

```
Rule
├── Priority          (lower number = evaluated first)
├── Effect            Allow | Deny | RequireApproval | RequireMfa
└── Conditions[]      Attribute, Operator, Value, IsValueAttributeRef
```

Operators are `Equals`, `NotEquals`, `LessThanOrEqual`, `GreaterThanOrEqual`, `In`.

When `IsValueAttributeRef` is true, `Value` is itself an attribute path
(`resource.department`) rather than a literal, which is how "your department must match
the resource's owning department" is expressed without any expression language.

**There is no expression parser and no code path in which policy input is compiled or
`eval`'d.** The builder in the Policy Studio posts attribute/operator/value triples. This
is a deliberate constraint, not a missing feature.

## Attribute namespace

| Prefix | Examples |
| --- | --- |
| `user.*` | `user.department`, `user.status`, `user.riskLevel`, `user.clearance`, `user.roles` |
| `resource.*` | `resource.classification`, `resource.department`, `resource.environment`, `resource.type` |
| `application.*` | `application.environment` |
| `request.*` | `request.action`, `request.environment`, `request.deviceTrust`, `request.authenticationStrength`, `request.riskLevel` |

`PoliciesPageController.AttributeCatalog` is the single source of this list for the UI,
so the builder cannot offer an attribute the engine does not know.

## Version lifecycle

Published versions are immutable. Changing a policy means creating a new version and
moving it through the state machine:

```
Draft ──▶ Review ──▶ Approved ──▶ Published ──▶ Deprecated
  ▲          │           │
  └──────────┴───────────┘        (any of these can also go to Deprecated)
```

| From | To |
| --- | --- |
| Draft | Review, Deprecated |
| Review | Approved, Draft, Deprecated |
| Approved | Published, Review, Deprecated |
| Published | Deprecated |
| Deprecated | — (terminal) |

Publishing is guarded:

- the version must contain **at least one rule** (an empty policy would default-deny
  everything it covers, which is almost always a mistake),
- the incumbent published version is retired **in the same transaction**, so there is
  never a moment with two published versions or none,
- `PublishedAtUtc` is stamped,
- the published-policy cache is invalidated so the next decision sees the change.

## Simulator

The simulator evaluates a draft version against a chosen subject/resource/action and
shows the before/after verdicts side by side. It runs the same engine with the same
attribute bag — it does not approximate it. The seeded demo tenant ships "Production
Database Access" with a published v1 and a draft v2 precisely so the difference is
visible.
