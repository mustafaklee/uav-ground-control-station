# ADR-015: Authentication, authorization and rate limiting

* Status: Accepted
* Date: 2026-10-07
* Phase: 8

## Context

Until Phase 7 anyone who could reach the API could do anything, and the command audit log recorded whatever name the
client typed in `X-Operator`. A ground control station commands real aircraft. We need to know **who** is acting, allow
each person **only** what their job needs, and slow down anyone guessing passwords or flooding the API.

Constraints:

* Operators sign in from the Avalonia desktop client. Later there may be scripts and other clients.
* Real-time data flows over SignalR (WebSockets), not only REST.
* There is no corporate identity provider in this project yet.
* Secrets must never be in the repository.

## Decision

### 1. Users in our own database, passwords hashed with ASP.NET Core Identity's hasher

`gcs.users` holds username (lower case, unique), password hash, one role, active flag, failed-login counter and lockout.

* We use `PasswordHasher<T>` from ASP.NET Core Identity (PBKDF2-HMAC-SHA512, 100 000 iterations, random salt,
  versioned format). We do not write our own cryptography. A hash in an old format is re-hashed transparently at login.
* Passwords need **12–128 characters** and nothing else: length beats composition rules.
* **Five wrong passwords lock the account for five minutes.** Together with the login rate limit, this makes online
  guessing hopeless.
* "Unknown user", "wrong password" and "deactivated" all answer with the same `401 auth.invalid_credentials`. An
  unknown user still costs one hash computation, so response time does not reveal valid usernames either. (Once a real
  account is locked, the different `auth.locked_out` message does reveal that it exists. We accept this: the operator
  needs to know why the right password fails.)
* Users are never deleted, only deactivated. The audit log refers to them by name.
* **The first administrator** is created at startup from `Security:BootstrapAdministrator:{Username,Password}`, and only
  while the user table is empty. The configured password can never reset an existing account.

We did not adopt the full ASP.NET Core Identity stack (UserManager, its tables, cookies). It brings a lot we do not use,
and its token flows are built for web apps. A dedicated identity provider (Keycloak, Entra ID) would be the next step if
the GCS has to join an organisation's single sign-on. The token validation below would stay the same.

### 2. Short JWT access tokens, rotating opaque refresh tokens

* **Access token:** a JWT signed with HMAC-SHA256, 15 minutes, carrying `sub` (user id), `name`, `role` and `jti`.
  Validation accepts only HS256 (no `alg: none`, no algorithm confusion), checks issuer, audience and lifetime, and
  allows 30 s of clock skew.
* **Refresh token:** 32 random bytes, not a JWT. It is stored as a SHA-256 hash and lives 12 hours (a shift).
  **Rotation:** every refresh revokes the token used and issues a new one. Presenting an already used token means a
  copy exists, so **all** of that user's refresh tokens are revoked (reuse detection). Logout, password change,
  deactivation and role change also revoke them.
* Why both? A stateless JWT is cheap to check on every request but cannot be revoked. A database-checked refresh token
  can be revoked but costs a query. Short JWTs plus revocable refresh tokens give "revoked within 15 minutes" at the
  price of one query per 15 minutes per user.
* **Known limitation:** a role change or deactivation takes effect when the current access token expires (at most
  15 minutes), not instantly. If instant revocation becomes necessary, add a per-user security stamp checked on each
  request (a cache lookup).
* **Signing key:** at least 32 bytes, from user-secrets (development), the `Jwt__SigningKey` environment variable
  (Docker, taken from `.env`) or a secret store. The API refuses to start without it. In Development only, a random key
  is generated with a warning, so `dotnet run` works out of the box. Rotating the key signs everyone out. HS256 is
  enough while one service issues and checks tokens. If other services need to verify tokens, switch to RS256/ES256 so
  they only need the public key.

### 3. Roles map to permissions; endpoints ask for permissions

Four roles, one per user: **Observer** (read only), **Operator** (flies), **Maintenance** (fleet registry) and
**Administrator** (everything). Endpoints never name roles. They require a **permission policy**, and one table in
`Permissions.cs` maps permissions to roles:

| Permission | Observer | Operator | Maintenance | Administrator |
|---|:-:|:-:|:-:|:-:|
| `read` (fleet, telemetry, missions, leases, audit, SignalR) | ✓ | ✓ | ✓ | ✓ |
| `vehicles.link` (connect/disconnect) | | ✓ | ✓ | ✓ |
| `missions.plan` (create/edit/upload, read a vehicle's mission) | | ✓ | | ✓ |
| `vehicles.command` (take control, all commands) | | ✓ | | ✓ |
| `vehicles.manage` (register/edit/retire) | | | ✓ | ✓ |
| `users.manage` | | | | ✓ |

* **Secure by default:** the fallback policy requires an authenticated user, so a new endpoint without a policy is
  closed, not open. Only health probes, `system/info` and login/refresh/logout are anonymous.
* Maintenance staff can connect a link to check a vehicle, but cannot command or plan. That is "critical commands
  require appropriate authorization" from the brief.
* The Administrator has every permission. That keeps a small installation workable with one account. Separation of
  duties (an administrator who may not fly) is one row change in the table if an operating organisation requires it.
* SignalR hubs require `read`. The token travels as `access_token` in the query string, because browsers cannot set
  headers on WebSockets, and it is accepted there only for `/hubs` paths.
* The command lease and the audit log now use the token's `name`. The `X-Operator` header is gone, so nobody can act
  under someone else's name.
* An `AuthorizationMatrixTests` theory calls every endpoint as every role (and anonymously) and checks 401/403
  against the table.

### 4. Rate limiting (ASP.NET Core rate limiter, fixed windows, no queueing)

| Limit | Default | Partition |
|---|---|---|
| Global | 600 requests/minute | per user, or per IP before sign-in. `/health` and `/hubs` are exempt |
| `auth` (login, refresh, logout) | 10/minute | per IP |
| `commands` (vehicle commands) | 60/minute | per user |

Rejections are `429` with `Retry-After` and code `http.rate_limited`. The counters are in memory, per API instance,
like the leases (ADR-014). A multi-instance deployment needs a shared limiter (Redis) or limits at the gateway.
Behind a reverse proxy, forwarded headers must be configured so the per-IP partition sees client addresses, not the
proxy's (Phase 11).

### 5. Transport and headers

* Every response gets `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  `Cross-Origin-Resource-Policy: same-origin` and `Content-Security-Policy: default-src 'none'` (the API serves JSON
  only).
* HSTS is on outside Development. TLS terminates at the reverse proxy or Kestrel certificate in deployment (Phase 11).
  The local compose stack binds to 127.0.0.1 and uses plain HTTP.
* The desktop client keeps tokens **in memory only**. Closing it ends the session, and nothing on disk can be copied.

## Consequences

* Every request is attributable to a person. The command audit log can no longer be spoofed.
* Roles can be tightened or loosened in one table, and a test proves the whole matrix.
* Operators sign in once per shift. Access tokens renew silently. A revoked or expired session sends the desktop back
  to the sign-in window.
* Leases, rate-limit counters and the development fallback key are per process. That is fine for one API instance and
  is listed for the multi-instance phase.
* Up to 15 minutes delay before a role change or deactivation takes effect.

## Alternatives considered

* **Cookies instead of bearer tokens.** Natural for browsers, awkward for a desktop client and SignalR from .NET, and it
  needs CSRF protection. Rejected.
* **Long-lived access tokens without refresh.** Simple, but impossible to revoke. Rejected.
* **Refresh tokens as JWTs.** Self-contained, but then rotation and reuse detection need a server-side list anyway.
  Opaque random tokens are simpler and safer.
* **Several roles per user / fine-grained permissions per user.** More flexible, but the matrix becomes untestable in
  practice. One role per user until a real need appears.
* **External identity provider now.** The right choice inside an organisation with SSO. Here it would add a container
  and configuration for no benefit yet. The JWT bearer validation makes switching later straightforward.
