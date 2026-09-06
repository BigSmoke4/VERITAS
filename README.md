# VERITAS

**Enterprise Identity, Access Governance & Zero-Trust Authorization Platform**

VERITAS answers a simple but difficult enterprise question:

> **Who is allowed to do what, to which resource, under which conditions — and can we prove why?**

VERITAS is a .NET 9 modular monolith built around PostgreSQL, ASP.NET Core Identity, Redis, policy evaluation, risk scoring, approvals, privileged access, access reviews, auditability, notifications, and an operator-facing Razor UI.

## Project status

The previously identified polish gaps are now completed:

- Swagger/XML documentation now covers webhook **list** and **delete** operations, including usable examples and response semantics.
- Access-review decisions have a real JSON API at `POST /api/v1/access-review/items/{itemId}/decision` with Swagger examples for Keep and Remove.
- Webhook list responses never expose the signing secret.
- ASP.NET Core Identity now has a branded, pre-built `/Identity/Account/Login` Razor Page instead of relying on the default Identity UI.
- The application cookie explicitly redirects unauthenticated users to the branded login page.
- Razor Pages are registered and mapped alongside the existing MVC/API routes.

**Build note:** this source package was inspected and modified in an environment without the .NET SDK or Docker CLI installed, so the final archive could not be compiled or executed here. The package is prepared for local verification; please run the build/test commands below on a machine with .NET 9 and Docker installed.

---

## What VERITAS implements

### 1. Multi-tenant authorization

- Modular monolith organized by bounded modules under `src/Veritas.Web/Modules`.
- PostgreSQL is the system of record through EF Core/Npgsql.
- Tenant-owned entities implement `ITenantOwned` and use global query filters.
- Tenant identity is carried by authenticated claims and resolved by `ITenantContext`.
- Cross-tenant reads are blocked at the data-access layer rather than depending only on controller checks.

### 2. Policy evaluation

`PolicyEvaluationEngine` provides a deterministic, deny-by-default authorization model with:

- RBAC and ABAC inputs.
- `ALLOW`, `DENY`, `REQUIRE_APPROVAL`, and `REQUIRE_MFA` outcomes.
- Priority-ordered, first-match rule evaluation.
- Immutable published policy versions.
- Reproducible authorization decisions tied to the exact `PolicyVersionId` used.

The primary decision endpoint is:

```text
POST /api/v1/authorize
```

It builds attributes from real database state, evaluates published policies, applies the risk gate, persists an authorization decision, records audit data, and returns the decision/reasons.

### 3. Risk-based authorization

`RiskEvaluationService` calculates explainable risk from real signals, including:

- Unrecognized IP.
- Recent failed logins.
- Privileged actions.
- Production environment.
- Outside-business-hours activity.
- Device trust.
- Location change compared with the last known login.

The authorization service applies the configured risk thresholds to policy results rather than silently ignoring risk.

### 4. Separation of Duties

Role assignment checks stored SoD conflict rules before creating a `UserRole` grant. Conflicting assignments are rejected and audited.

The demo tenant includes a realistic conflict between:

- `payment.create`
- `payment.approve`

### 5. Approval workflows

The approval module supports real persisted approval steps and multiple strategies:

- Sequential.
- All required.
- Any one.

Concurrency is handled through EF/PostgreSQL optimistic concurrency on protected entities.

### 6. Temporary and privileged access

- JIT/temporary grants are persisted in PostgreSQL.
- Temporary grants expire through `TemporaryAccessExpirationWorker` rather than an in-memory timer.
- Privileged access has grant/revoke/check operations and audit trails.
- Temporary grants are capped at 24 hours by validation.

### 7. Access reviews

Access reviews are real database workflows, not mock screens.

A campaign can be generated from current role grants. Each item can be reviewed as:

- `Keep`
- `Remove`
- `Modify`
- `Delegate`

A `Remove` decision revokes the underlying role grant.

The API endpoint is:

```text
POST /api/v1/access-review/items/{itemId}/decision
```

Example:

```json
{
  "decision": "Remove",
  "note": "No longer required for the current role.",
  "delegateToUserId": null
}
```

Successful decisions return `204 No Content`.

The operator UI is available at:

```text
/AccessReviewsPage
```

### 8. Service accounts and API keys

API keys use a one-time-secret model:

- Cryptographically random secret generation.
- SHA-256 hash stored in the database.
- Plaintext secret returned only during creation.
- Rotation and revocation support.

The UI never re-displays an API-key secret after creation.

### 9. Policy simulator

`POST /api/v1/policies/simulate` evaluates a candidate policy against the same authorization engine used for live decisions and reports access changes rather than using fabricated estimates.

### 10. Access graph

The access graph is derived from real:

- Users.
- Roles.
- Permissions.
- Temporary grants.
- Resources.

The UI and JSON API share the same application query service so they do not implement separate authorization-graph logic.

### 11. Webhooks and notifications

Webhook subscriptions are persisted per tenant and consumed by the notification transport.

API:

```text
GET    /api/v1/webhook-subscriptions
POST   /api/v1/webhook-subscriptions
DELETE /api/v1/webhook-subscriptions/{id}
```

Example creation request:

```json
{
  "eventType": "ACCESS_APPROVED",
  "targetUrl": "https://example.com/hooks/veritas",
  "secretForSigning": "a-shared-secret"
}
```

Webhook payloads can be signed with HMAC-SHA256 using `X-Veritas-Signature`.

**Security detail:** the signing secret is never included in the list response and is not written to normal logs.

The Razor management page is:

```text
/WebhookSubscriptionsPage
```

### 12. Audit and security detection

The platform records security-relevant actions and authorization outcomes in the audit store.

`SecurityDetectionWorker` detects repeated authorization denials in a rolling window and emits a security event without generating duplicate alerts for the same window.

The UI includes:

- Audit Explorer.
- Security Control Center dashboard.

Both show real data and explicitly display an empty state when there is no data.

### 13. Notifications/outbox

Notifications use a persisted outbox model:

1. The triggering operation creates a notification row.
2. `NotificationWorker` processes pending rows.
3. Delivery uses the pluggable `INotificationTransport` abstraction.
4. Attempts, failures, and timestamps are persisted.

The included email transport logs delivery rather than pretending an external SMTP provider exists.

### 14. Redis

Redis is used for:

- Published-policy caching.
- Policy generation invalidation.
- Authorization idempotency keys.

Redis is intentionally not the source of truth. If Redis is unavailable, the application can fall back to PostgreSQL or allow the request path to continue where the feature is designed to fail open.

### 15. Rate limiting and idempotency

Separate rate-limit policies exist for authorization, login, administrative APIs, and access-request APIs.

`POST /api/v1/authorize` supports an `Idempotency-Key` header to prevent accidental duplicate processing.

### 16. ASP.NET Core Identity

Identity is backed by the real EF Core Identity tables and uses:

- 12-character minimum passwords.
- Uppercase requirement.
- Non-alphanumeric requirement.
- Five failed attempts before lockout.
- Fifteen-minute lockout duration.
- Confirmed-email sign-in requirement.
- Security-stamp validation every five minutes.
- Tenant-aware claims including `org_id`, lifecycle state, and display name.

#### Branded login

Unauthenticated browser requests are redirected to:

```text
/Identity/Account/Login
```

The page is implemented in:

```text
src/Veritas.Web/Areas/Identity/Pages/Account/Login.cshtml
src/Veritas.Web/Areas/Identity/Pages/Account/Login.cshtml.cs
```

It uses the real `SignInManager<ApplicationUser>` and therefore preserves Identity's password validation, lockout, confirmed-email behavior, and security-stamp lifecycle. The page is only a presentation layer over the existing authentication system.

### 17. Swagger/OpenAPI

Swagger UI is available at:

```text
/swagger
```

XML documentation is generated from the web project and loaded into Swashbuckle.

Worked examples are included for the important mutating APIs and the less-central endpoints that were previously missing coverage, including:

- Webhook list.
- Webhook creation.
- Webhook deletion.
- Access-review decisions.
- Authorization.
- Policy simulation.
- Privileged access grants.
- API-key creation.

### 18. Health and security hardening

Health endpoints:

```text
GET /health/live
GET /health/ready
```

The readiness endpoint performs a real PostgreSQL connectivity check.

The application also configures:

- HTTPS redirection.
- Secure HTTP-only cookies.
- SameSite cookie protection.
- HSTS outside Development.
- `X-Content-Type-Options`.
- Referrer Policy.
- Permissions Policy.
- Content Security Policy.
- Optimistic concurrency for protected entities.

---

## Demo data

Development startup seeds one realistic tenant automatically when the database is empty for the demo organization.

**Organization:** Apex Financial Group

The seed contains:

- 3 departments.
- 3 applications.
- 3 resources.
- 10 permissions.
- 4 roles.
- 4 users.
- 1 Separation-of-Duties conflict.
- 1 published three-rule policy.

### Demo accounts

| User | Email | Password | Role |
|---|---|---|---|
| Sarah Khan | `sarah.khan@apexfinancial.example` | `CorrectHorse!Battery1` | Finance Manager |
| John Smith | `john.smith@apexfinancial.example` | `CorrectHorse!Battery2` | Payment Approver |
| Priya Natarajan | `priya.natarajan@apexfinancial.example` | `CorrectHorse!Battery3` | Security Administrator |
| Marcus Webb | `marcus.webb@apexfinancial.example` | `CorrectHorse!Battery4` | Auditor |

These credentials are **development/demo credentials only**. Do not use them in a production deployment.

The seeder is idempotent and only runs automatically in Development.

---

## Requirements

- .NET 9 SDK.
- Docker Engine/Desktop.
- Docker Compose.
- PostgreSQL 16 when running outside Compose.
- Redis 7 when enabling the Redis-backed features locally.

---

## Run locally

### 1. Configure environment

```bash
cp .env.example .env
```

Set a real `POSTGRES_PASSWORD` in `.env`.

### 2. Restore and build

```bash
dotnet restore
dotnet build
```

### 3. Run unit tests

```bash
dotnet test tests/Veritas.Tests
```

### 4. Run integration tests

Integration tests use Testcontainers and require Docker:

```bash
dotnet test tests/Veritas.IntegrationTests
```

### 5. Optional benchmarks

```bash
dotnet run -c Release --project tests/Veritas.Benchmarks
```

Benchmark numbers are intentionally not documented here because they depend on the machine running them.

---

## Database migrations

Generate the EF Core migration using the provided script:

```bash
./scripts/generate-migrations.sh
```

Then apply migrations with the standard EF command if required by your environment:

```bash
dotnet ef database update --project src/Veritas.Web --startup-project src/Veritas.Web
```

For the Development/Compose profile, the application calls `EnsureCreatedAsync()` before seeding so a fresh PostgreSQL volume is usable without a separately generated migration. Production deployments should generate and apply real EF migrations.

---

## Run with Docker Compose

```bash
docker compose up --build
```

Default endpoints:

```text
Application: http://localhost:8080
Swagger:     http://localhost:8080/swagger
Login:       http://localhost:8080/Identity/Account/Login
Health:      http://localhost:8080/health/live
Readiness:   http://localhost:8080/health/ready
```

The container listens on port `8080`.

To stop the stack:

```bash
docker compose down
```

To remove the PostgreSQL volume as well:

```bash
docker compose down -v
```

---

## Useful API examples

### Authorization

```bash
curl -X POST http://localhost:8080/api/v1/authorize \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: demo-001" \
  -d '{
    "subject": "<user-guid>",
    "resource": "<resource-guid>",
    "action": "read",
    "environment": "production",
    "context": {
      "ip": "10.0.0.5",
      "deviceTrust": "high",
      "authenticationStrength": "strong"
    }
  }'
```

### List webhooks

```bash
curl http://localhost:8080/api/v1/webhook-subscriptions
```

### Delete a webhook

```bash
curl -X DELETE http://localhost:8080/api/v1/webhook-subscriptions/<subscription-guid>
```

### Record an access-review decision

```bash
curl -X POST http://localhost:8080/api/v1/access-review/items/<item-guid>/decision \
  -H "Content-Type: application/json" \
  -d '{
    "decision": "Keep",
    "note": "Access remains required.",
    "delegateToUserId": null
  }'
```

### Simulate a policy change

```bash
curl -X POST http://localhost:8080/api/v1/policies/simulate \
  -H "Content-Type: application/json" \
  -d '{
    "candidatePolicyVersionId": "<draft-version-guid>",
    "resourceId": "<resource-guid>",
    "action": "read"
  }'
```

For the complete request/response documentation, use `/swagger`.

---

## Project layout

```text
VERITAS/
├── src/
│   └── Veritas.Web/
│       ├── Areas/Identity/Pages/Account/   # branded Identity UI
│       ├── Modules/                         # bounded application modules
│       ├── BackgroundWorkers/
│       ├── Infrastructure/
│       ├── Observability/
│       ├── Shared/
│       ├── Views/                           # Razor operator UI
│       └── wwwroot/                         # CSS/static assets
├── tests/
│   ├── Veritas.Tests/                       # unit tests
│   ├── Veritas.IntegrationTests/            # Testcontainers tests
│   └── Veritas.Benchmarks/                  # BenchmarkDotNet
├── docs/
│   ├── architecture.md
│   └── adr/
├── scripts/
├── .github/workflows/ci.yml
├── Dockerfile
├── docker-compose.yml
├── Veritas.sln
└── README.md
```

---

## Architecture principles

VERITAS deliberately favors a modular monolith rather than prematurely splitting the platform into microservices.

The important invariants are:

1. **PostgreSQL is the source of truth.**
2. **Redis is an optimization/cache, not an authority.**
3. **Authorization is deny-by-default.**
4. **Published policy versions are immutable.**
5. **Authorization decisions record the exact policy version used.**
6. **Tenant boundaries are enforced in the data layer.**
7. **Security-sensitive actions are auditable.**
8. **UI and API paths reuse the same application services.**
9. **No endpoint should return fabricated success data.**
10. **External delivery is asynchronous where appropriate.**

Detailed architecture decisions are documented under `docs/adr/`.

---

## CI

`.github/workflows/ci.yml` is intended to run:

1. Restore.
2. Build.
3. Formatting checks.
4. Analyzer-as-error build.
5. Unit tests.
6. Integration tests using Testcontainers.
7. Vulnerable-package checks.
8. Docker build.
9. A manually approved production-deployment placeholder.

Wire the final deployment step to the registry/orchestrator used by your environment before treating the workflow as a production deployment pipeline.

---

## Verification checklist

After extracting this archive, run:

```bash
dotnet restore
dotnet build
dotnet test tests/Veritas.Tests
dotnet test tests/Veritas.IntegrationTests
docker compose up --build
```

Then verify:

- `/Identity/Account/Login` renders the branded login page.
- A seeded development account can sign in.
- `/swagger` shows webhook GET/POST/DELETE examples.
- `/swagger` shows the access-review decision endpoint and examples.
- Webhook list output does **not** contain `secretForSigning`.
- A webhook can be deleted with `204 No Content`.
- An access-review `Remove` decision revokes the corresponding role grant.
- `/health/live` returns `200`.
- `/health/ready` returns `200` when PostgreSQL is reachable.

---

## License

No license file is currently included. Add the appropriate license before publishing the repository publicly.
