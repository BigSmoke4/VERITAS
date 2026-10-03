# Deployment

## Container

```bash
docker build -t veritas .
docker run -p 8080:8080 \
  -e ConnectionStrings__Postgres="$PG" \
  -e ConnectionStrings__Redis="$REDIS" \
  veritas
```

The image:

- restores all four projects before `dotnet restore` (a missing csproj fails the restore
  with MSB3202),
- runs as a non-root user (UID 1001),
- exposes `8080`,
- declares a `HEALTHCHECK` against `/health/live`.

## Compose

`docker compose up` starts PostgreSQL 16, Redis 7 and the application. See
[database.md](database.md) for why Development creates the schema directly while
Production applies migrations.

## Configuration

Every setting is bound to an options class in
`Shared/Application/Configuration/VeritasOptions.cs`; each declares its own
`SectionName` constant and all are bound in `Program.cs`.

| Section | Purpose |
| --- | --- |
| `Veritas:Database` | connection behaviour, `AutoMigrate` |
| `Veritas:Authorization` | decision defaults and risk gating |
| `Veritas:Policy` | policy cache lifetime |
| `Veritas:AccessRequest` | maximum grant duration, chain rules |
| `Veritas:Risk` | band thresholds |
| `Veritas:Workers` | per-worker poll intervals |
| `Veritas:Notification` | transports and retry |
| `Veritas:Security` | headers, rate limits |

Credentials come from configuration or the environment. `Program.cs` refuses to start if
`ConnectionStrings:Postgres` is empty, so a misconfigured deployment fails loudly at boot
rather than serving traffic against the wrong database.

## Release process

`.github/workflows/release.yml` is dispatched manually with a semantic version. It runs
the full test suite, then reaches a job bound to the `production` **GitHub Environment**.
That environment is configured with required reviewers, so the job pauses until a human
approves it. Nothing is built, pushed to GHCR or tagged until that approval happens.

## Post-deploy

1. Confirm `/health/ready` is green — it performs a real database round-trip.
2. Confirm `/health/live` is green.
3. Issue one `POST /api/v1/authorize` and check the decision appears in the Audit
   Explorer. That verifies the write path end to end, not just the read path.
