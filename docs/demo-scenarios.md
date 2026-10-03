# Demo scenarios

Three scenarios exercise the platform end to end. Each ends with an audit record that
proves what happened.

---

## 1. Temporary production database access, start to finish

**The situation.** An incident requires read access to the Production Payment Database.
The engineer does not hold `payment.read` permanently and should not.

1. Sign in as **John Smith** (`john.smith@apexfinancial.example` /
   `CorrectHorse!Battery2`). He holds `production.logs.read` and deliberately **not**
   `payment.read`.
2. Call `POST /api/v1/authorize` for `payment.read` on the Production Payment Database.
   The result is **Deny**, and the transcript names the missing permission. Note the
   `decisionId`.
3. Open **Access Requests → Request access**. Request `payment.read` on that resource for
   30 minutes with justification `INC-20482 — investigate reconciliation discrepancy`.
4. Because the resource is `HIGHLY_CONFIDENTIAL` **and** in `production`, the chain is
   **Manager → SecurityAdministrator**.
5. Sign in as **Sarah Khan** (Finance Manager) and approve. The request advances; no grant
   is issued yet.
6. Sign in as **Priya Natarajan** (Security Administrator) and approve. A real
   `TemporaryGrant` is now issued with a 30-minute expiry, and `PRIVILEGED_ACCESS_GRANTED`
   is audited with `CorrelationId = grant.Id`.
7. Repeat step 2. The result is now **Allow**, and the transcript names the temporary
   grant as the source of the permission.
8. Wait for the expiry (or shorten the duration), then repeat step 2 once more. The result
   is **Deny** with the reason `Temporary access expired.`, and the access request has
   moved to `Expired` with `ACCESS_GRANT_EXPIRED` audited.

**What it proves.** Access was granted for a stated reason, for a bounded time, through a
two-person chain — and the system can show, from the audit trail alone, who approved it,
why, for how long, and when it stopped working.

---

## 2. Separation-of-Duties violation is refused

**The situation.** An operator who can raise payments attempts to gain the ability to
approve them.

1. Sign in as **Sarah Khan** (Finance Manager — holds `payment.create`).
2. Go to **Identity → Sarah Khan** and try to assign the **Payment Approver** role
   (which carries `payment.approve`).
3. The assignment is **refused**. The screen reports the conflicting permission pair
   `payment.create` ⊘ `payment.approve`, and `ROLE_ASSIGNMENT_DENIED_SOD` is audited.
4. Try the same outcome through the other door: submit an access request for
   `payment.approve`. It is rejected at submission with `ACCESS_REQUEST_DENIED_SOD`,
   before any approver sees it.

**What it proves.** SoD is enforced in the single code path that writes role assignments,
not in the UI. Both routes to the same end state are blocked, and both refusals are
audited — the refusal is as much a record as the grant would have been.

---

## 3. Policy change, simulated before it is live

**The situation.** The security team wants to tighten production database access and
needs to know the effect before publishing.

1. Go to **Policy Studio → Production Database Access**. Version **v1** is `Published`;
   **v2** is `Draft`.
2. Open **Simulate** against John Smith reading the Production Payment Database from a
   production environment with `deviceTrust = LOW`. With **v1** the decision is **Allow**.
3. Switch the simulated version to **v2** and re-run. The decision is **Deny**, and the
   transcript shows the rule that produced it.
4. Move **v2** through `Review → Approved → Published`. Publishing retires **v1** in the
   same transaction and invalidates the policy cache.
5. Re-run the original authorization request. The live decision now matches the
   simulation exactly.

**What it proves.** The simulator runs the same engine, against the same attribute bag, as
the live path — it is not an approximation. And because published versions are immutable
and retirement is transactional, there is never a window in which two versions are live or
none is.
