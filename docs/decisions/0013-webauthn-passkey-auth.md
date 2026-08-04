# 0013 — Authentication: WebAuthn / passkeys with multi-credential and recovery codes

* Status: Accepted
* Date: 2026-05-08

## Context

Coffer needs an authentication scheme. The constraints, in order:

1. **Must work when the home internet is down.** The home server is still
   reachable over LAN / VPN, and "I can't see my own books because my ISP is
   out" is an unacceptable failure mode for a personal finance app.
2. **Single user, but the user has multiple devices** — phone, primary laptop,
   YubiKeys (one daily, one offline backup). A scheme that supports only one
   credential leaves the user a single point of failure.
3. **Phishing-resistant.** A finance app exposed over the internet (even if
   gated by a VPN or proxy) shouldn't rely on a typed shared secret as the
   primary credential.
4. **Minimal maintenance.** No password reset emails, no rate-limiter tuning,
   no breach-monitoring duties. The user wants to build the app, not run an
   auth service.

The first constraint rules out **OIDC against any public IdP** (Google,
Microsoft, GitHub) as the primary scheme — fresh login requires reaching the
IdP, and that's not available in a WAN outage. Tokens already issued continue
to validate locally for their lifetime, but cold-start authentication fails.

Self-hosted IdPs (Authelia, Authentik, Keycloak) satisfy (1) by living on the
same Docker stack, but at significant maintenance cost (2 — a non-trivial
container to operate, configure, and update) for what would otherwise be
modest in-app code.

Local password (single hashed credential) satisfies (1) and (4) but is weak on
(3) and gives a worse UX than modern alternatives.

## Decision

**WebAuthn / FIDO2 is the primary authentication mechanism.** Specifically:

- **Credential types accepted:** any WebAuthn authenticator. YubiKey is the
  user's primary credential; platform passkeys (Windows Hello, Touch ID,
  Android passkey, iPhone passkey) are first-class equivalents.
- **Multiple credentials per account.** No upper bound enforced. Each
  registered credential records:
  - The FIDO2 credential ID and COSE-encoded public key.
  - The signing counter (replay-attack mitigation per WebAuthn spec).
  - The authenticator AAGUID (lets the UI label "YubiKey 5C" vs. a phone
    passkey vs. a software passkey).
  - The reported transports (`usb`, `nfc`, `ble`, `internal`, `hybrid`).
  - A user-supplied nickname.
  - `created_at` / `last_used_at`.
- **Recovery codes.** At setup the user is given **10 single-use recovery
  codes**, displayed once, hashed with **Argon2id** in the database. Each
  code is good for one re-registration: present a code, then enroll a new
  credential. Codes are regeneratable from an authenticated session
  (regeneration invalidates all prior codes).
- **Bootstrap.** On first start, when no credentials exist, the API generates
  a one-time setup token and writes it to the container's stdout/log. The
  operator shares `/setup/{token}` with the first user. The setup page calls
  `GET /api/auth/setup/{token}/info` on mount: the server validates the
  token (still unconsumed, unexpired) and returns the list of ledgers the
  new user could join. The user picks a username, display name, passkey
  nickname, and a **ledger** — either an existing one (becoming its owner,
  typically the seeded `Default`) or a new one created on their behalf in
  the same transaction. The bootstrap token is consumed on `/complete`;
  subsequent registrations require an authenticated session or a recovery
  code. Recovery codes are returned plaintext exactly once from `/complete`
  and the SPA surfaces them via Copy / Download .txt / Print affordances
  behind an explicit acknowledgement gate before routing onward to
  `/welcome`.
- **Sessions.** Cookie-based (`HttpOnly`, `Secure`, `SameSite=Strict`); the
  cookie carries an opaque session id whose hash is stored in
  `auth_sessions`. Defaults: 30-day max lifetime, 7-day idle timeout,
  multiple concurrent sessions allowed. UI shows active sessions and
  supports "sign out everywhere".

### Implementation outline

- Library: [Fido2.AspNet](https://github.com/passwordless-lib/fido2-net-lib) —
  the maintained .NET FIDO2 library.
- Schema (lands in the Phase 3 migration, not retroactively in Phase 1):
  - `users` — single row for now; the table exists so multi-user is a future
    schema change rather than a future redesign.
  - `webauthn_credentials` — one row per registered authenticator.
  - `recovery_codes` — Argon2id-hashed, `used_at` for one-shot semantics.
  - `auth_sessions` — token hash, expiry, idle, user-agent.
- API surface (Minimal API endpoints):
  - `GET  /api/auth/setup/{token}/info` — validate the bootstrap token and
    return the joinable ledger list. Used by the SPA on mount so an invalid
    or already-consumed token surfaces before the user fills the form.
  - `POST /api/auth/setup/{token}/begin` — mint a WebAuthn registration
    challenge for the supplied username + display name.
  - `POST /api/auth/setup/{token}/complete` — verify attestation, create the
    user + credential + recovery codes + ledger grant (single transaction),
    consume the bootstrap token, and set the session cookie.
  - `POST /api/auth/register/begin` — request registration challenge for an
    authenticated session.
  - `POST /api/auth/register/complete` — verify attestation, store
    credential.
  - `POST /api/auth/login/begin` — assertion challenge.
  - `POST /api/auth/login/complete` — verify assertion, issue session
    cookie.
  - `POST /api/auth/login/recovery` — accept a username + recovery code,
    consume it, and issue a session cookie (rate-limited per client IP).
    Re-keying then uses the authenticated `/register` flow below — simpler
    than the originally-sketched "recovery code returns a registration
    challenge" (one mechanism for adding a passkey, not two).
  - `GET  /api/auth/credentials` — list / nickname / created / last-used.
  - `DELETE /api/auth/credentials/{id}` — remove a credential (refuses the
    user's last one — removing it would lock them out).
  - `GET  /api/auth/recovery-codes` — remaining count (never the codes).
  - `POST /api/auth/recovery-codes/regenerate` — issue a new set; invalidate
    the old.
  - `POST /api/auth/logout` — invalidate the current session.
  - `POST /api/auth/sessions/revoke-all` — invalidate every session.

### Update (2026-06-24): recovery + passkey management shipped

Setup and login shipped in Phase 3; the rest of the surface above landed
now, motivated by [ADR-0061](0061-bootstrap-restore.md): restoring a backup
onto a host with a different WebAuthn **RP id** (e.g. a new domain) makes
every stored passkey unusable — a credential is cryptographically bound to
the RP id it was created under — so an account-recovery path that doesn't
depend on a passkey became a hard requirement, not a nicety.

- **Recovery sign-in** (`/login/recovery`) verifies the code (constant-time
  Argon2id over the unused hashes), consumes only the matching row, and
  issues a session. It is **rate-limited** (fixed window per client IP,
  `Api:Auth:RecoveryRateLimitPerMinute`, default 10): unlike an assertion, a
  recovery code is a bearer secret and each attempt is an expensive Argon2id
  verify, so the limit caps both brute-force and the memory-DoS the verify
  cost would otherwise enable. Failures are a generic 401 (no enumeration).
- **Passkey management** is authenticated and current-user-scoped. Adding a
  passkey uses a new `register` challenge flow (migration 140 widens the
  `webauthn_pending_challenges.flow` CHECK) and excludes the user's existing
  credentials. Delete refuses the **last** passkey (atomic correlated-EXISTS
  guard) so a user can't lock themselves out.
- The SPA exposes all of this: a "use a recovery code" link on login, and an
  `/account/security` page (passkeys + recovery codes), which is also the
  landing spot after a recovery sign-in (prompting a re-key).

### Local-development carve-out

Authentication is enforced from Phase 3 onward in **all environments**. For
local development a `Development`-only auth handler bypass is gated by
`DEV_AUTH=1` and a `Development` `ASPNETCORE_ENVIRONMENT`. Both must be
present; the production build path always validates real WebAuthn assertions.
This is a recognized .NET pattern, not a "skip auth" code path.

## Consequences

**Positive**
- Works fully offline. The auth ceremony is server ↔ browser ↔ device
  crypto; no IdP dependency.
- Phishing-resistant by design (no shared secret to capture).
- No password storage burden. The DB holds public keys and Argon2id hashes
  of recovery codes.
- Real UX: tap your YubiKey, use Touch ID, scan a QR with your phone — no
  typing.
- Self-contained: no extra container, no IdP to operate, no third-party
  service to depend on for daily logins.

**Negative**
- We own the WebAuthn integration. `Fido2.AspNet` keeps it bounded but it's
  still real code.
- A user who loses every registered authenticator **and** every recovery
  code is permanently locked out. This is intentional — any backdoor would
  defeat the security model. The mitigation is the scheme itself: register a
  YubiKey, a backup YubiKey kept offline, and at least one platform passkey;
  store recovery codes in your password manager.
- Adding multi-user later requires expanding the `users` table and adding
  per-user authorization on every endpoint. The schema makes this a forward
  change, not a redesign.

## Alternatives considered

- **OIDC against a public IdP (Microsoft / Google / GitHub).** Fails the
  offline-must-work constraint for fresh logins. Rejected as the primary
  mechanism. Could be added as a *secondary* method later (e.g., for
  registering a new device when away from your YubiKey on a working
  internet), via the `external_logins` extension table. Out of Phase 3 scope.
- **Self-hosted IdP (Authelia / Authentik / Keycloak).** Solves the offline
  requirement at heavy operational cost. Worth it for users who run many
  self-hosted services and want SSO across them; not worth it for a
  single-app deployment. Rejected for now; if Coffer ever lives next to other
  self-hosted services that already share an IdP, this can supersede.
- **Local password + Argon2id + rate-limit.** Simpler in code but worse on
  UX and security (typed secret, brute-force surface, leak risk). The
  WebAuthn implementation cost is bounded enough that "simpler code" doesn't
  outweigh "no shared secret". Rejected.
- **Hybrid: WebAuthn + password fallback.** Two auth systems to maintain.
  The recovery-code path already covers the "lost all my devices" case
  without introducing a typed password into the threat model. Rejected.
- **Cloudflare Access at the edge.** Solid, free for personal use, but it
  adds a third-party dependency on Cloudflare and requires routing the
  domain through them. The user does not currently use Cloudflare; standing
  it up is a larger change than implementing WebAuthn. Rejected.
- **No auth, rely on network posture (Teleport / VPN).** The user explicitly
  asked that network posture and authentication remain independent concerns.
  Defense in depth says authenticate even on trusted networks. Rejected as a
  *substitute*; remains a complementary control.
