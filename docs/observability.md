# Observability

## Logging

Serilog writes structured JSON to the console. Every log entry carries the correlation id
from the current request, so an entry can be joined to the audit row it corresponds to.

## Health checks

| Endpoint | Tag | Behaviour |
| --- | --- | --- |
| `/health` | — | Aggregate of all registered checks |
| `/health/live` | `live` | No dependencies touched. Answers whether the process is running |
| `/health/ready` | `ready` | Performs a real PostgreSQL round-trip via `DatabaseHealthCheck` |

The readiness check reports **Degraded** rather than Unhealthy when the database is
unreachable, so a transient database blip does not cause an orchestrator to kill and
restart a process that would recover on its own.

Redis health is reported but does not fail readiness: Redis is latency infrastructure, and
a platform that cannot reach its cache is still able to authorize correctly (ADR-003).

## Metrics and tracing

OpenTelemetry is configured in `Program.cs` with ASP.NET Core, `HttpClient` and Entity
Framework Core instrumentation, exporting to the console by default. The meter name is
exposed as `VeritasMetrics.MeterName` so a collector can be pointed at it without
duplicating the string.

## What is not instrumented

The control-centre dashboard shows a **P95 latency** figure. When there is no real
measurement available, that value is `null` and the UI renders "no data". It is never
filled with an estimate or a placeholder number — a latency figure the platform did not
measure would be a fabrication, and the platform's entire value proposition is that its
numbers are real.
