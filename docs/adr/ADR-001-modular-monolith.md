# ADR-001: Modular Monolith over Microservices

## Status
Accepted

## Context
VERITAS must own identity, RBAC, ABAC, policy evaluation, access requests,
approvals, risk, and audit — modules that are read together on almost every
request (an authorize call touches user, resource, policy, and audit state
in one transaction-adjacent flow). Microservices would mean distributed
transactions or eventual consistency for data that needs to be
read-your-writes consistent (e.g. "policy just published, next authorize
call must see it").

## Decision
Single deployable ASP.NET Core application, single PostgreSQL database,
organized into modules with enforced boundaries (Domain/Application/
Infrastructure/Presentation per module, no cross-module DbContext access).

## Consequences
- Simpler transactions, simpler deployment, simpler local dev.
- Module boundaries are enforced by convention/code review today; extracting
  a module (e.g. Authorization) into a service later means promoting its
  Application-layer interfaces to a network contract — the interfaces
  already exist (`IAuthorizationService`, `IPolicyEvaluationEngine`), so the
  seam is pre-built.
- Requires discipline: nothing outside `Modules/Authorization` may reference
  `Modules/Authorization/Domain` types directly.
