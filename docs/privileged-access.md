# Privileged and just-in-time access

## Temporary grants

A `TemporaryGrant` is a scoped, time-boxed permission for one subject against one
resource. It carries a business justification, a start time, an expiry, and — if revoked
early — who revoked it and why.

`IPrivilegedAccessService`:

```csharp
Task<TemporaryGrant> GrantTemporaryAccessAsync(
    Guid userId, Guid resourceId, string permissionKey, TimeSpan duration, string reason, ...);

Task<bool> HasActiveGrantAsync(Guid userId, Guid resourceId, string permissionKey, ...);
```

Issuing a grant audits `PRIVILEGED_ACCESS_GRANTED` with `CorrelationId = grant.Id`, so a
grant and the decisions made under it are joinable in the audit log.

## How grants are enforced

Grants are not a report. The authorization pipeline consults them at the
`PERMISSION_HELD` check: an active grant satisfies the permission requirement exactly as
a static role would. An expired or revoked grant produces a deny naming the lapse.

`CK_TemporaryGrant_Window` guarantees `ExpiresAtUtc > StartAtUtc` at the database level.

## Expiry is enforced by a worker

`TemporaryAccessExpirationWorker` runs on a schedule, finds grants whose window has
closed, transitions the owning access request to `Expired`, and audits
`ACCESS_GRANT_EXPIRED`. It uses `IgnoreQueryFilters()` because it runs outside any
HTTP request and therefore has no resolved tenant.

The duration requested is capped by the configured maximum temporary-grant duration, and
`CK_AccessRequest_Duration` enforces the same bound in the database.

## Revocation

Revocation is immediate: the next authorization decision sees `Revoked == true` and
denies. There is no cache of entitlements that outlives a revocation.
