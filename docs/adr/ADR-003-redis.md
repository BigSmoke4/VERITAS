# ADR-003: Redis for Caching, Rate Limiting, and Coordination — Not Source of Truth

## Status
Accepted

## Decision
Redis holds: published-policy cache (versioned by PolicyVersionId so a stale
cache entry is simply never referenced again after a new version publishes),
rate-limit counters, idempotency-key results, and distributed locks for
operations like temporary-access expiration where two worker instances must
not double-process the same grant.

## What happens if Redis fails
Policy/permission reads fall back to PostgreSQL (slower authorize calls, not
incorrect ones). Rate limiting and idempotency degrade to "fail open with a
warning log" rather than blocking all traffic, because availability of the
authorization path matters more than perfect rate limiting during a cache
outage — this trade-off should be revisited if abuse becomes a concern.
