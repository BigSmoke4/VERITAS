# Identity

## Lifecycle states

An account moves through an explicit state machine. Transitions are validated against
`UserLifecycleState.AllowedTransitions`, so an illegal move is refused rather than
recorded.

```
INVITED ──▶ PENDING_VERIFICATION ──▶ ACTIVE ──┬──▶ SUSPENDED ──▶ ACTIVE
                                              └──▶ REVOKED  (terminal)
```

Only `ACTIVE` accounts can be the subject of an authorization decision — the pipeline
rejects any other state at the `ACCOUNT_ACTIVE` check. Suspension and revocation are
therefore real enforcement, not UI cosmetics.

## Roles and permissions

A permission key is always `resource.action`, lower-cased, and is validated by the
`CK_Permission_Key_Shape` check constraint. Permissions are grouped by the resource's
`PermissionKeyPrefix`, so a resource registered with prefix `payment` yields
`payment.read`, `payment.create`, `payment.approve`, `payment.delete`.

The permissions screen shows a real holder count per permission, and flags permissions
with zero holders as **unused** — that count is what feeds the `UNUSED_PERMISSION`
compliance control.

## Role assignment is a single code path

There is exactly one place that writes a `UserRole` row, and it returns a
`RoleAssignmentOutcome` rather than throwing:

```csharp
Task<RoleAssignmentOutcome> AssignRoleAsync(Guid userId, Guid roleId, DateTimeOffset? expiresAtUtc, ...);
// RoleAssignmentOutcome(bool Succeeded, string? Error)
```

Before writing, the assignment is evaluated against every Separation-of-Duties rule. If
the target role's permission set would conflict with a permission the user already holds,
the assignment is **refused** and the refusal is audited as
`ROLE_ASSIGNMENT_DENIED_SOD`. On success it is audited as `ROLE_ASSIGNED`.

This matters because the obvious alternative — validating SoD in the UI and letting the
service write — means every new caller is a potential bypass. Here, identity
administration, access-request approval and the JSON API all funnel through the same
method, so there is no back door.

## Separation of Duties

An SoD rule names two permission keys that one subject must not hold simultaneously. The
seeded demo tenant defines:

- `payment.create` ⊘ `payment.approve` — nobody both raises and approves a payment
- `customer.modify` ⊘ `audit.read` — nobody both edits records and reads the audit trail

Rules are enforced at assignment time and re-checked when an access request is
submitted, so a conflict cannot be introduced through either path.

## Service identities

Non-human principals (`ServiceAccount`) authenticate with API keys. Only the SHA-256
hash of a key is stored; the plaintext is returned **exactly once**, at creation or
rotation, through `TempData` and is never logged. Rotation revokes the old key in the
same operation.
