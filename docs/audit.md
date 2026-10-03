# Audit

## What is audited

Every state change that affects who may do what writes an `AuditLog` row. `IAuditService`
is the only writer; nothing writes audit rows ad hoc.

Audited actions include, non-exhaustively:

| Action | Trigger |
| --- | --- |
| `AUTHORIZATION_ALLOWED` / `AUTHORIZATION_DENIED` | every decision from `/api/v1/authorize` |
| `ROLE_ASSIGNED` / `ROLE_ASSIGNMENT_DENIED_SOD` | role assignment, including refusals |
| `ACCESS_REQUESTED` | an access request was submitted |
| `ACCESS_REQUEST_DENIED_SOD` | a request was refused on SoD grounds |
| `ACCESS_APPROVED` / `ACCESS_DENIED` | an approval decision |
| `PRIVILEGED_ACCESS_GRANTED` | a temporary grant was issued |
| `ACCESS_GRANT_EXPIRED` | the expiration worker closed a grant |
| `USER_LIFECYCLE_TRANSITION` | an account changed state |
| `API_KEY_ISSUED` / `API_KEY_ROTATED` / `API_KEY_REVOKED` | service-identity key operations |
| `POLICY_PUBLISHED` / `POLICY_DEPRECATED` | policy lifecycle moves |

`CK_AuditLog_Action_NotEmpty` guarantees no row is written without an action name.

## What a row contains

Actor, action, resource, previous and new values where the action is a change, the source
IP, and a `CorrelationId`.

## Correlation

`CorrelationId` is the join that makes an audit trail readable. A temporary grant is
written with `CorrelationId = grant.Id`; the decisions taken under that grant carry the
same id. Following one id therefore yields the grant, the request that produced it, the
approvals behind that request, and every authorization decision it enabled.

## Append-only

Audit rows are never updated or deleted by application code. There is no endpoint that
edits or removes an audit record, and the `CleanupWorker` does not touch the audit table.

## The explorer

The Audit Explorer filters by action, actor, resource, correlation id, source IP and UTC
time range. Filtering and paging are executed in PostgreSQL — the server never loads a
table into memory to page it. CSV export uses the same query path with a row cap.

Decision rows link through to the stored transcript, so the explanation shown is the one
produced at decision time, not a reconstruction from current data.
