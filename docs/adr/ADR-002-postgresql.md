# ADR-002: PostgreSQL as the Single Source of Truth

## Status
Accepted

## Context
Authorization decisions, policy versions, and audit records must be durable,
queryable with real joins/filters, and support optimistic concurrency.

## Decision
PostgreSQL via Npgsql/EF Core. Redis is a cache only, never authoritative.

## Consequences
- If Redis is unavailable, the system must still function correctly by
  falling back to PostgreSQL reads for policy/permission lookups — slower,
  not wrong. (Cache-aside, not cache-required.)
- Optimistic concurrency uses Postgres's `xmin` system column, mapped via
  `IsRowVersion()`, for Policy, PolicyVersion, and AccessRequest — the
  entities most likely to be edited concurrently by two administrators.
