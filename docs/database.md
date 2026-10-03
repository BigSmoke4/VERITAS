# Database

PostgreSQL is the system of record for everything. Redis is latency infrastructure only
(ADR-003): if Redis is unreachable the platform keeps working against PostgreSQL.

## Context

`VeritasDbContext` (`Shared/Infrastructure/VeritasDbContext.cs`) is a single context with
roughly 35 `DbSet`s spanning all 18 modules. That is a deliberate modular-monolith
trade-off (ADR-001) — module boundaries are enforced by convention, not by separate
assemblies or separate databases.

## Multi-tenancy

Every tenant-owned entity implements `ITenantOwned` (a single `OrganizationId`). The
context registers EF Core global query filters for:

1. **Tenant isolation** — `e.OrganizationId == _tenant.OrganizationId`, applied only when
   `ITenantContext.IsResolved` is true, so cross-tenant reads are structurally
   impossible for request-scoped code.
2. **Soft delete** — `!e.IsDeleted` for entities deriving from `AuditableEntity`.

Background workers run outside any HTTP request and therefore have no resolved tenant.
They opt out explicitly with `IgnoreQueryFilters()` and filter by organisation
themselves. This is intentional and every such call site says so in a comment.

`DesignTimeVeritasDbContextFactory` supplies an **unresolved** tenant context at design
time. Migration scaffolding never queries, and a factory that invented an organisation id
would falsely imply migrations are tenant-scoped.

## Constraints that are enforced in the database

Invariants that matter are check constraints, not application conventions:

| Constraint | Guarantees |
| --- | --- |
| `CK_TemporaryGrant_Window` | `ExpiresAtUtc > StartAtUtc` |
| `CK_AccessRequest_Duration` | duration is positive and within the configured maximum |
| `CK_RiskEvaluation_Score` | score is 0…100 |
| `CK_Permission_Key_Shape` | permission keys match `resource.action` |
| `CK_ApiKey_Hash_Length` | stored key hashes are exactly 64 hex characters |
| `CK_AuditLog_Action_NotEmpty` | an audit row always names an action |
| `CK_Organization_Slug_NotEmpty` | tenants are addressable |

Row versioning uses PostgreSQL's `xmin` system column rather than a hand-maintained
version column, so optimistic concurrency is enforced by the database.

## Migrations

**Migrations are not checked in.** A hand-written migration that drifts from the real
model snapshot is worse than no migration at all, because it fails silently. Instead:

- `scripts/generate-migrations.sh` generates a migration from the current model and
  applies it. Commit the generated `src/Veritas.Web/Migrations/` folder afterwards.
- `DesignTimeVeritasDbContextFactory` lets the EF tooling construct the context outside
  the application's DI container.
- CI runs `dotnet ef migrations has-pending-model-changes`, so an entity change without a
  regenerated migration fails the build.

### Development vs production

| Environment | `Database:AutoMigrate` | Behaviour |
| --- | --- | --- |
| Development | `false` | `EnsureCreatedAsync()` builds the schema from the current model, then the demo seeder runs |
| Production | `true` | `MigrateAsync()` applies pending migrations. `EnsureCreated` is never called |

`EnsureCreated` exists purely so `docker compose up && dotnet run` is a two-command
experience. It is not a production path and it does not interoperate with migrations.
