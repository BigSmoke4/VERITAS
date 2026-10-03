# Security

## Transport and headers

Security headers are set centrally in `Program.cs` for every response:

- `Content-Security-Policy` with `script-src 'self'` and `style-src 'unsafe-inline'`.
  Scripts are external ES modules; inline scripts would be blocked, and the views contain
  none. `unsafe-inline` for styles is required by Razor tag helpers that emit inline
  `style` attributes.
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`
- `Referrer-Policy: no-referrer`
- `Strict-Transport-Security` in Production

## Logout

Sign-out is a **POST** to `/Identity/Account/Logout` with an antiforgery token. There is
no GET logout link, because a GET logout is a cross-site request an attacker can trigger
with an `<img>` tag.

## Secrets

| Secret | Handling |
| --- | --- |
| Database and Redis connection strings | Configuration or environment only. `Program.cs` throws on startup if `ConnectionStrings:Postgres` is empty; no credential is committed |
| API keys | SHA-256 hash stored; plaintext shown once at issue/rotation and never logged |
| Webhook signing secrets | Never returned by any endpoint. Delivery is signed with HMAC-SHA256 in the `X-Veritas-Signature` header |
| Identity data-protection keys | Configured through ASP.NET Core's data-protection API |

`.env.example` documents the required variables with placeholder values only.

## Tenancy

Cross-tenant reads are blocked by EF Core global query filters keyed on
`ITenantContext.OrganizationId`, so tenant isolation is a property of the data-access
layer rather than a check each controller must remember to perform.

## Privilege separation

The container runs as a non-root user (UID 1001). Diagnostics collection is disabled in
the production image.

## Threat model notes

- **Policy injection** — the policy engine has no expression language. Policy input is
  structured data validated against a fixed attribute catalogue, so there is no input
  that could be compiled or evaluated.
- **Approval races** — approval steps use `xmin` optimistic concurrency. Two concurrent
  approvals yield one decision and one visible failure, never two grants.
- **Audit tampering** — no application code path updates or deletes an `AuditLog` row.
- **Entitlement caching** — grants are read at decision time. A revocation takes effect
  on the next request; there is no cache whose TTL outlives it.
