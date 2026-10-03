# Access requests and approvals

## Submitting a request

`AccessRequestService.SubmitAsync` does three things in order:

1. **Refuses Separation-of-Duties breaches up front.** If the requested permission would
   conflict with something the requester already holds, the request is rejected before
   any approver sees it, and `ACCESS_REQUEST_DENIED_SOD` is audited.
2. **Scores the request with the real risk engine**, so the approver sees the same
   number the platform will act on later — not an estimate.
3. **Materialises the approval chain** as persisted `ApprovalStep` rows.

The chain depends on the resource:

| Condition | Approval chain |
| --- | --- |
| Resource is `HIGHLY_CONFIDENTIAL` **or** in `production` | Manager → SecurityAdministrator |
| Otherwise | Manager → ResourceOwner |

Requests are audited as `ACCESS_REQUESTED` on submission.

## Deciding

`ApprovalWorkflowService.SubmitDecisionAsync` validates that the caller is acting in the
role the current step requires, records the decision with its comment, and advances the
chain.

- **On the final approval**, a real `TemporaryGrant` is issued with the expiry implied by
  the requested duration, and `ACCESS_APPROVED` is audited. The request moves to
  `Granted`.
- **On a denial**, the chain terminates at `Denied` and `ACCESS_DENIED` is audited.
- **Concurrent decisions are rejected by optimistic concurrency**, not silently merged.
  Two approvers clicking at once produces one decision and one `DbUpdateConcurrencyException`
  surfaced as a clean failure — never a double grant.

## The approval queue

The queue shows every request in an in-flight status — `Requested`, `ManagerReview`,
`ResourceOwnerReview`, `SecurityReview`. Showing only `Requested` would make a request
disappear from view the moment the first approver acted on it, which is exactly when the
next approver needs to find it.

## Status values

`Requested`, `ManagerReview`, `ResourceOwnerReview`, `SecurityReview`, `Approved`,
`Denied`, `Granted`, `Expired`.

A granted request becomes `Expired` when its temporary grant lapses; the
`TemporaryAccessExpirationWorker` performs that transition and audits
`ACCESS_GRANT_EXPIRED`.
