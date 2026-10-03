# Authorization

`POST /api/v1/authorize` is the platform's central decision point. Every other screen —
the decision console, the audit explorer, the dashboard — reads decisions this endpoint
produced.

## The pipeline

Decisions are produced by `AuthorizationService.FinalizeAsync` after a fixed sequence of
checks. Each check emits a machine-readable code into the explanation transcript.

| Order | Check | Code | Failure result |
| --- | --- | --- | --- |
| 1 | Request identifiers are well formed | — | `400 Bad Request` |
| 2 | Tenant resolved | `TENANT_VERIFIED` | deny |
| 3 | Subject exists in the tenant | `SUBJECT_EXISTS` | deny |
| 4 | Account is `ACTIVE` | `ACCOUNT_ACTIVE` | deny |
| 5 | Resource exists | `RESOURCE_EXISTS` | deny |
| 6 | Subject holds the required permission (RBAC) | `PERMISSION_HELD` | deny |
| 7 | Published policies loaded | `POLICY_LOADED` | deny |
| 8 | At least one policy matched | `POLICY_MATCH` | deny (default-deny) |
| 9 | Risk is within tolerance | `RISK_WITHIN_TOLERANCE` | deny |

`IDENTITY_VERIFIED` is emitted first, before the tenant check.

### Step 6 in detail

The required permission key is built from the resource's own prefix:
`{Resource.PermissionKeyPrefix}.{action}`, lower-cased. A subject satisfies it if either:

- a **static role** they hold grants the permission, or
- an **active temporary grant** grants it.

If neither holds but an expired or revoked grant exists for the same
subject/resource/permission, the decision is a deny with the reason
`Temporary access expired.` — the most recent such grant is named in the transcript.
This distinction matters for audits: "never had access" and "had access, it lapsed" are
different findings.

### Step 9 in detail

The risk gate can only **tighten** a decision (`ApplyRiskGate`). A policy that allows
something can be overridden by risk; a policy that denies something can never be
overridden into an allow by a low risk score. Risk is a constraint, not an entitlement.

## Persistence

Every decision — allow, deny, or "approval required" — is persisted as an
`AuthorizationDecisionRecord` together with an `AuditLog` row carrying the same
`CorrelationId`. Nothing is decided and discarded. The transcript of checks is stored
with the decision so the explanation you see weeks later is the explanation that was
produced at the time, not a reconstruction.

## Idempotency

`POST /api/v1/authorize` accepts an `Idempotency-Key` header. Replaying the same key
returns the original response rather than producing a second decision, so a client retry
cannot double-count a privileged action.
