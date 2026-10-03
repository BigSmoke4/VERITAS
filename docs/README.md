# VERITAS documentation

VERITAS is an enterprise identity, access-governance and zero-trust authorization
platform. The core question it answers is:

> Who is allowed to do what, to which resource, under which conditions — and can we
> prove why, after the fact?

## Reading order

| Document | What it covers |
| --- | --- |
| [getting-started.md](getting-started.md) | Running the platform locally, demo credentials, first request |
| [architecture.md](architecture.md) | Layering, module boundaries, the modular-monolith trade-off |
| [database.md](database.md) | Schema conventions, multi-tenancy filters, migrations vs `EnsureCreated` |
| [identity.md](identity.md) | Users, lifecycle states, roles, permissions, Separation of Duties |
| [authorization.md](authorization.md) | The `/api/v1/authorize` pipeline and how explanations are produced |
| [policy-engine.md](policy-engine.md) | Policy versions, the lifecycle state machine, rule structure |
| [access-requests.md](access-requests.md) | Requests, approval chains, and how a grant is actually issued |
| [privileged-access.md](privileged-access.md) | Temporary grants, JIT access, expiry enforcement |
| [risk-engine.md](risk-engine.md) | Signals, scoring, bands, and the impact/likelihood matrix |
| [compliance.md](compliance.md) | Controls, findings, deduplication, security-event detection |
| [audit.md](audit.md) | What is audited, correlation, the audit explorer |
| [api.md](api.md) | HTTP surface, authentication, idempotency, rate limits |
| [security.md](security.md) | Headers, secrets, key handling, threat model |
| [observability.md](observability.md) | Structured logging, health checks, OpenTelemetry |
| [frontend.md](frontend.md) | The CSS/JS conventions the UI must follow |
| [deployment.md](deployment.md) | Docker, compose, configuration, the release gate |
| [testing.md](testing.md) | What each test project proves, and how to run them |
| [demo-scenarios.md](demo-scenarios.md) | The three end-to-end scenarios, step by step |
| [roadmap.md](roadmap.md) | What is **PLANNED** and deliberately not implemented |
| [adr/](adr/) | Architecture decision records ADR-001 … ADR-006 |

## A rule that applies to every page

VERITAS never fabricates data. A risk score, an audit record, a latency figure or a
benchmark number appears in the UI only because something real produced it. Where a
capability is designed but not built, it is labelled **PLANNED** rather than
simulated. See [roadmap.md](roadmap.md).
