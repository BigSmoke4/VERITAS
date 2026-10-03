# HTTP API

## Authentication

The JSON API accepts either:

- an **API key** in the `X-Api-Key` header, issued to a `ServiceAccount` and scoped by
  `ApiKeyScope`, or
- an **ASP.NET Core Identity cookie** for browser sessions.

Only the SHA-256 hash of a key is stored (`CK_ApiKey_Hash_Length` enforces 64 hex
characters). Keys are returned in plaintext exactly once, at issue or rotation.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/authorize` | Request an authorization decision with a full explanation |
| `GET` | `/api/v1/webhook-subscriptions` | List webhook subscriptions (signing secret never returned) |
| `POST` | `/api/v1/webhook-subscriptions` | Create a subscription (target must be `https`) |
| `DELETE` | `/api/v1/webhook-subscriptions/{id:guid}` | Delete a subscription |
| `POST` | `/api/v1/access-review/items/{itemId}/decision` | Record a Keep or Remove decision on a review item |
| `GET` | `/health`, `/health/live`, `/health/ready` | Health probes |

Swagger is served at `/swagger` with XML documentation, request/response examples and
status-code annotations.

## Rate limiting

Rate limiting is configured per named policy in `Program.cs` and applied by attribute, so
each surface is limited independently:

| Policy | Applies to |
| --- | --- |
| `authorization-api` | `POST /api/v1/authorize` — per caller, the tightest limit |
| `access-request-api` | access-request submission |
| `apikey-auth` | API-key authentication attempts |
| `login` | interactive sign-in |
| `admin-api` | administrative controllers, including the audit surface |
| `audit-api` | the Audit Explorer |

The authorization endpoint is limited per caller rather than globally, because it is the
one endpoint an attacker has a reason to hammer and a global limit would let one abusive
client deny the service to every other.

When Redis is configured it backs the distributed limiter; when it is not, limiting falls
back to the in-process implementation and PostgreSQL remains the correctness boundary
(ADR-003).

## Idempotency

Mutating endpoints accept an `Idempotency-Key` header. The first request with a given key
is executed and its response recorded; replays return the recorded response. This is what
allows a client to retry a privileged action safely after a timeout without risking a
double grant.

## Errors

Validation failures return `400` with a problem-details body naming the offending fields.
Authorization failures return a `200` with `result: "Deny"` — a deny is a valid answer to
a question, not an error — except where the request itself is malformed or the subject or
resource does not exist, which are `400`/`404`.
