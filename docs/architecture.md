# Architecture

```
Razor View / API Client
        |
        v
MVC Controller / ApiController   (thin: DTO mapping + status codes only)
        |
        v
Application Service              (IAuthorizationService, IAuditService, ...)
        |
        v
Domain                            (entities, PolicyEvaluationEngine — pure logic)
        |
        v
Infrastructure                    (VeritasDbContext -> PostgreSQL; Redis planned)
```

## Module boundary rule

A module's controllers/services may only depend on:
- its own `Domain`/`Application`/`Infrastructure`
- other modules' `Application`-layer **interfaces** (e.g. `IAuthorizationService`)
- `Shared/*`

A module must never reference another module's `Domain` entities or
`Infrastructure` (DbContext) directly. Today all modules share one
`VeritasDbContext` for simplicity (modular monolith, ADR-001) — the
boundary is enforced by convention and code review, not by separate
assemblies, which is a known trade-off worth revisiting if the team grows.

## Authorization request flow (implemented)

```
POST /api/v1/authorize
   -> AuthorizationApiController (maps DTO)
   -> AuthorizationService.AuthorizeAsync
        -> load User, Resource from VeritasDbContext (tenant-filtered)
        -> build AttributeBag from real column values
        -> load Published PolicyVersions + Rules + Conditions
        -> PolicyEvaluationEngine.Evaluate (pure, unit-tested)
        -> persist AuthorizationDecisionRecord (with PolicyVersionId)
        -> IAuditService.RecordAsync (AUTHORIZATION_ALLOWED/DENIED)
   -> AuthorizeResponseDto returned to caller
```

## Tenant isolation (implemented)

`ITenantContext.OrganizationId` is resolved from the `org_id` claim on the
authenticated principal. `VeritasDbContext.OnModelCreating` applies a global
EF Core query filter (`HasQueryFilter`) to every entity implementing
`ITenantOwned`, so a LINQ query that "forgets" to filter by organization
still can't return another tenant's rows — the filter runs regardless.
Tenant isolation should be covered by an explicit integration test
(`TenantIsolationTests`, listed as PLANNED in the README) before this is
considered verified end-to-end.
