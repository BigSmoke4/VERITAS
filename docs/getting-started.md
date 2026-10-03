# Getting started

## Prerequisites

- .NET 9 SDK
- Docker (for the PostgreSQL and Redis containers, and for the integration tests)

## Run it

```bash
docker compose up -d postgres redis
./scripts/generate-migrations.sh          # first run only
dotnet run --project src/Veritas.Web --launch-profile https
```

In Development the application creates the schema directly from the current EF model
and seeds the demo tenant, so `docker compose up` plus `dotnet run` is enough to get a
usable system. Production never does this — see [database.md](database.md).

Browse to `/` and sign in with any demo account below.

## Demo tenant

The seeded organisation is **Apex Financial Group** (`apex-financial-group`). Every
demo user's password is `CorrectHorse!Battery<N>` where `<N>` is 1–10.

| User | Email | Role | Notes |
| --- | --- | --- | --- |
| Sarah Khan | sarah.khan@apexfinancial.example | Finance Manager | holds `payment.read`, `payment.create` |
| John Smith | john.smith@apexfinancial.example | Incident Responder | holds `production.logs.read` — deliberately **not** `payment.read` |
| Priya Natarajan | priya.natarajan@apexfinancial.example | Security Administrator | can approve security-review steps |
| Daniel Osei | daniel.osei@apexfinancial.example | — | |
| Marcus Webb | marcus.webb@apexfinancial.example | Release Manager | already has an active 4-hour `production.logs.read` grant |
| Elena Fischer | elena.fischer@apexfinancial.example | — | |
| Aisha Rahman | aisha.rahman@apexfinancial.example | — | |
| Tomás Alvarez | tomas.alvarez@apexfinancial.example | — | has a **pending** `risk.read` request awaiting approval |
| Grace Liu | grace.liu@apexfinancial.example | Auditor | |
| Robert Mbeki | robert.mbeki@apexfinancial.example | Payment Approver | holds `payment.approve` |

Seeding is deterministic: the request history is generated from `Random(20260101)`, so
two machines seeded from the same model produce the same 240 authorization records.

## First authorization call

```bash
curl -X POST http://localhost:5000/api/v1/authorize \
  -H 'Content-Type: application/json' \
  -d '{
    "subject": "john.smith@apexfinancial.example",
    "resource": "<Production Payment Database id>",
    "action": "read",
    "environment": "production",
    "requestId": "demo-1"
  }'
```

The response is a **deny**, with a transcript explaining that the subject does not hold
`payment.read`. That is the intended first impression of the platform: the explanation
is the product. The same decision is written to the audit log and is visible in the
Audit Explorer under the returned `decisionId`.

Walk through [demo-scenarios.md](demo-scenarios.md) for the three full scenarios.

## Useful endpoints

| Path | Purpose |
| --- | --- |
| `/swagger` | Interactive API documentation |
| `/health` | Aggregate health of every registered check |
| `/health/live` | Liveness — no dependencies touched |
| `/health/ready` | Readiness — performs a real PostgreSQL round-trip |
