# Testing

## Projects

| Project | What it proves |
| --- | --- |
| `tests/Veritas.Tests` | Pure logic: the policy engine, RBAC evaluation, lifecycle transitions, SoD evaluation, validation. No I/O |
| `tests/Veritas.IntegrationTests` | Real behaviour against real infrastructure: PostgreSQL 16 and Redis 7 boot through Testcontainers |
| `tests/Veritas.Benchmarks` | BenchmarkDotNet harnesses for the hot paths |

## Running

```bash
dotnet test tests/Veritas.Tests
dotnet test tests/Veritas.IntegrationTests   # needs Docker
dotnet run -c Release --project tests/Veritas.Benchmarks
```

## What the integration suite covers

- The full temporary-access lifecycle: request → approve → grant → authorize succeeds →
  expire → authorize denies.
- Separation-of-Duties refusal on both the role-assignment and access-request paths.
- Policy publish/retire atomicity — there is never a moment with two published versions.
- Tenant isolation through the global query filters.

`TestTenantContext.Current` is an `AsyncLocal<Guid>`. `EndToEndTemporaryAccessTests`
deliberately never sets it, so both sides of the comparison are `Guid.Empty` — the test
exercises the unresolved-tenant path on purpose.

## Benchmarks

Benchmarks are **compiled** in CI but never executed. A BenchmarkDotNet run takes many
minutes and its numbers are meaningless on shared CI hardware. Publishing a number the
platform did not actually measure would violate the project's central rule, so the
harness is build-verified here and run deliberately, on dedicated hardware, when a real
number is needed.

## Rules the test suite is held to

- Functionality is never removed to make a test pass.
- Errors are never suppressed without the cause being understood and documented.
- A test that asserts a fabricated value is worse than no test.

---

## What was verified without a compiler

This repository was assembled in a sandbox with no .NET SDK, no NuGet access and no
Docker CLI. `dotnet build` and `dotnet test` could not be run there. Rather than present
the result as verified, here is exactly what *was* checked mechanically, and what a real
build still has to confirm.

### Checked mechanically

| Check | Scope | Method |
| --- | --- | --- |
| Brace and parenthesis balance | all 107 `.cs` files under `src/` and `tests/` | lexer-aware scan that strips comments, string and char literals before counting |
| View model types exist | all 33 distinct `@model` declarations across 43 views | every type resolved to a declaration in the source tree |
| Controller and action references resolve | every `asp-controller` / `asp-action` pair in every view | each pair matched against a declared controller class and a declared action method |
| Form fields match action parameters | the structured policy builder, approvals, identity, roles, resources, compliance and service-identity forms | posted field names compared against the target action's parameter list |
| View members exist on their model | record and class members referenced by each new view | each member compared against the declaring type |
| CSS coverage | 147 statically-used classes across all views | every class matched to a selector in `wwwroot/css/` — zero unmatched |
| JS contract | every `data-*` attribute emitted by a view | each one consumed by a module under `wwwroot/js/` — zero orphans |
| CI and release workflows | `.github/workflows/*.yml` | parsed as YAML; job and step structure inspected |

### Still to be confirmed by a real build

The checks above cannot catch what only a compiler can: overload resolution, nullable
annotation mismatches, EF query translation failures at runtime, Razor generated-code
errors, or a member that exists with a different signature than the one a view assumed.
Run:

```bash
dotnet build Veritas.sln
dotnet test tests/Veritas.Tests
dotnet test tests/Veritas.IntegrationTests
```

CI runs all three plus the EF model-drift check and a Docker build on every push, so the
first real execution is not a manual step anyone has to remember.
