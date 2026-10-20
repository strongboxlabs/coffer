# 0105 — A cookie alone is not enough, and disabling someone ends what they hold

* Status: Accepted (amends 0013, 0063, 0083, 0092); amended 2026-10-19 (FU-087, FU-095)
* Date: 2026-10-13
* Related: the 2026-10-12 security audit (H1, H2, M1, M2, M6, S3, S5, S6, S7, and the
  setup-complete `no-store` from Info). The audit's remaining findings are tracked in
  the open-work backlog.

## Context

The audit found that a stolen or forged session could do almost anything the person it
belonged to could, and that the controls meant to stop it either were not there or did
not hold:

- **Step-up covered two surfaces.** ADR-0092's `FreshAssertionGate` guarded the master
  key and passphrase reveals. Adding a passkey, minting an MCP token, granting admin,
  restoring a backup and turning on MCP writes needed only the cookie. The gate itself
  accepted any of the user's passkeys, including one the same session had just enrolled,
  and accepted user verification "preferred", so a PIN-less key passed (H1/M2, S7).
- **Disable was a sign-in block, not a revocation.** A disabled user kept their sessions,
  MCP tokens and OAuth grants. A disabled admin still passed `current_app_user_is_admin()`
  and could re-enable themselves (H2).
- **Invites outlived the issuer's authority.** A link stayed redeemable after the person
  who issued it was disabled, demoted or removed from the ledger (M6).
- **OAuth consent showed the client's own words.** The consent screen rendered the client
  name and redirect from the query string, accepted a `javascript:` redirect at
  registration despite ADR-0063 saying "https or loopback only", and approved by default
  through `form_post` (H1).
- **CSRF protection was SameSite=Strict alone,** which a sibling subdomain passes (M1).
- **Setup re-armed** whenever the passkey count reached zero, exposing the pre-auth
  whole-database restore on a populated install (S3).

## Decision

### D1 — Sensitive actions each need their own tap

Nine actions need a fresh passkey assertion made for that action:
`passkey-add`, `passkey-delete`, `recovery-codes`, `mcp-token`, `grant-admin`,
`restore`, `backup-passphrase`, `backup-delete`, `mcp-writes`. The client calls
`POST /api/auth/step-up/begin {action}`, runs the ceremony, and sends the result as
base64url JSON in the `X-Coffer-Step-Up` header of the request it guards. A header, not a
body field, because restore is multipart and regenerating recovery codes has no body.
Each action has its own challenge flow (`step-up:<action>`, migration 248), so a tap
approves exactly one kind of request, once, within two minutes.

ADR-0092's reveals and every new action go through the same `FreshAssertionGate`, now
with two more conditions:

- **The passkey predates the session.** A session that enrolled a passkey cannot use it
  to approve anything. Otherwise a stolen cookie enrolls the attacker's key and then
  taps with it.
- **User verification is required** (`requireUserVerification` on the ceremony, and the
  UV bit checked on the result). For step-up only; ordinary sign-in is unchanged.

Two principals skip the tap. Dev auth (ADR-0013's dual gate) has no passkey. A session
that began with a recovery code may add **one** passkey within 15 minutes of signing in,
because a user who lost every key must be able to enroll one. The allowance is recorded
on the challenge and checked again when the ceremony completes.

### D2 — Sessions record how they began

`auth_sessions.auth_method` (`passkey`, `recovery-code`, `setup`, `invite`, `dev`) is
stamped at issue and carried on the principal with the session id and start time. That
is how D1 knows which sessions get the recovery allowance and which passkeys predate them.

When the cookie is `Secure` its name gains the `__Host-` prefix
(`__Host-coffer.session`), so a sibling subdomain cannot plant or shadow it. The rename
signs everyone out once, on the upgrade that introduces it.

### D3 — Disabling ends everything the user holds

Disable revokes every session, MCP token, OAuth authorization and OAuth token in the same
request. Every credential check (session, MCP bearer, OAuth refresh and code exchange)
and `current_app_user_is_admin()` also refuse a disabled user. That covers what is
minted in the same instant, or flipped by any future path that skips the revoke.

Admins get **Sign out** (`POST /api/admin/users/{id}/sign-out`), which revokes the same
set without disabling. Users get **Sign out other sessions**
(`POST /api/auth/sessions/sign-out-others`), which keeps the current one. This is half
of ADR-0013's "UI shows active sessions and supports sign out everywhere"; the session
list is still owed.

### D4 — An invite is only as good as its issuer, at redemption

An invite is valid only while its issuer is enabled and still has the authority it used.
An admin backs any invite. Anyone else backs only what an owner can send: an invite to a
ledger they still own, granting no admin. So a deployment invite (no ledger), which only
the admin endpoint issues, dies with its issuer's admin flag.
This is checked when the invite is redeemed, not only when it is issued. Disabling,
demoting or changing a member's role also deletes the invites they can no longer back.

### D5 — Consent shows what the server holds

The consent page no longer reads anything descriptive from its own URL. When the
authorize request reaches the API, the server mints a **consent ticket**: in memory,
single-use, valid for 10 minutes, and bound to the user, client, redirect URI, scopes and
PKCE challenge. The page fetches the client name, redirect host and scopes by ticket.
The POST must say `deny`, or `allow` with a ticket that matches the request; anything
else is a 400. `form_post` is removed as a response mode. Redirect URIs must be https, or http
to a loopback host, at registration and again at authorize; that is what ADR-0063 said
was already the rule.

### D6 — Cookie-carrying writes must come from our own origin

`SameOriginRequestMiddleware` refuses an unsafe request that carries the session cookie
unless `Sec-Fetch-Site` is `same-origin`, or, where that header is absent, `Origin` is
the request's own origin or one of `Api:Fido2:Origins`. A request with neither header is
not from a browser, and CSRF does not apply to it. Bearer-token requests are untouched.

### D7 — Setup completes once

Setup writes `system_settings['setup.completed']` in the transaction that creates the
first user. Migration 248 backfills it on any install that already has a real user.
Bootstrap tokens are issued only while the marker is absent. Deleting every passkey no
longer reopens the pre-auth setup and restore surface. This amends ADR-0013's "when no
credentials exist" and ADR-0083's "only while zero credentials exist".

### D8 — Smaller fixes, same pass

- Expired WebAuthn challenges are swept on the scheduler tick (S5).
- Setup-complete, recovery-code regenerate and MCP-token mint responses are `no-store`.
- The post-login `returnUrl` is parsed the way the browser will parse it, and followed
  only to `/oauth/authorize` on this origin, the one place the server ever sends it (S6).

### D9 — One clock

Everything in `src/Api` reads time through an injected `TimeProvider`: the wall clock,
elapsed time and timers. Before this, 147 direct reads made "now" impossible to place in
a test. Several were not even the process clock: `DateTime.UtcNow` inside an EF lambda
is translated to the database's `now()`, so the session idle timeout compared a Postgres
timestamp with a process one. Those reads now go to the app clock, sent as parameters.
Session start and passkey creation, which D1 compares, are stamped from it too, instead
of column defaults. `ClockDisciplineTests` fails the build on a direct read, on
`TimeProvider.System` anywhere except its one registration, and on a delay or timer that
is not handed the clock.

## Consequences

- An admin's routine work now asks for taps: making someone an admin, restoring, changing
  the backup passphrase, deleting a backup, enabling MCP writes. That is the point. Each
  tap is scoped to one request, so there is no "elevated for five minutes" window to
  misuse.
- A user whose only passkey was enrolled in their current session cannot step up until
  they sign in again. The error says so.
- Everyone signs in again once, after the upgrade (D2).
- Consent tickets live in memory, so a restart between the authorize redirect and the
  approval makes the user start the connection again. The page says the request expired.
- SQL that reads the clock itself is unchanged: column defaults (`DEFAULT now()`) still
  stamp `created_at` on other tables, including the transaction and leg `created_at` that
  canonical ordering (ADR-0034) and bulk selection (ADR-0024) read, and the register's
  `CURRENT_DATE` fallback still applies when no local date is sent. D9 moved only reads
  written in C#, and stamps the API compares against its own clock.

## Alternatives considered

- **A step-up "elevation" that lasts N minutes.** Fewer taps, but a stolen cookie inside
  the window has everything. Per-action taps cost one ceremony per sensitive request and
  cannot be replayed onto another action.
- **Antiforgery tokens instead of the origin check.** Equivalent protection, but every
  client (SPA, multipart uploads, body-less posts) would have to carry one, and a missed
  endpoint fails open. The browser already sends the headers the middleware checks.
- **Gating setup on "no real users" at query time.** Correct, but untestable in a shared
  database where other tests' users exist. A persisted marker is explicit and survives
  the case where every user is deleted.
- **Requiring user verification everywhere.** It would lock out existing PIN-less keys at
  sign-in. Required for step-up only, where the stakes justify it.

## Amendment, 2026-10-19 — what the first pass left (FU-087, FU-095)

**Sessions are listed, and a credential change ends the others (FU-087).** D3 left
ADR-0013's session list owed. *Account → Security → Sessions* now lists your live
sessions: the browser, when it signed in, and when it was last active. The one you are
using is marked. Each of the others can be ended on its own
(`DELETE /api/auth/sessions/{id}`). Removing a passkey or regenerating recovery codes now
ends every other session too, because the passkey or code being retired may be what
opened them.

**An OAuth grant ends 90 days after consent (FU-087).** Refresh tokens are sliding, so an
app that kept refreshing kept its access for ever, and the user could not see it. A grant
now lasts 90 days from the authorization that created it, however often the app
refreshes (`OAuthGrants.MaxAge`). Past that, the token endpoint revokes the grant and
refuses it with `invalid_grant`, and the next authorize asks for consent again. *Connected
apps* lists the apps you signed in to beside your MCP tokens: whether each can make
changes, when it connected and when it expires. **Disconnect** revokes the grant and
every token under it. RFC 7009
revocation is served at `/oauth/revoke`. New clients get the permission at registration,
and clients registered before it are given it once at boot.

**The OAuth nits (FU-095).**
- `coffer.read` is required at `/mcp`, not only advertised. `IgnoreScopePermissions`
  lets a client ask for any scope, so a grant of none of Coffer's could read every ledger
  its user sees. Authorize now grants `coffer.read` with whatever the client asked for. A
  token without it gets 403 `insufficient_scope` naming the scope, the signal a client
  re-authorizes on. Answering 401 would have called a valid token invalid.
- `prompt=none` answers `login_required` or `consent_required` to the client instead of
  showing a page, as OpenID Connect requires.
- `prompt=login` is satisfied by a sign-in made in the last five minutes. Honouring it on
  every visit sent a user who had just signed in back to the login page.
- Fido2's exception text is logged, not returned.
- The signature counter is written compare-and-set against the value the assertion was
  verified against. Of two concurrent assertions with one counter, only one signs in.

**Every endpoint says who may call it (FU-095).** There was no fallback policy, so a
route without `RequireAuthorization()` was open, and `/openapi/v1.json` was anonymous in
production.
- The fallback policy is now the default one: a signed-in user.
- `EndpointAuthorizationCatalogTests` holds every route to declaring one or the other,
  and names each anonymous route with its reason.
- The SPA shell is one of those routes, since a signed-out browser loads it to reach the
  login page.
- `/openapi` is mapped in Development only.

**D4 narrows.** "An admin backs any invite" is now "an admin backs any invite that names
no ledger". A ledger invite needs an owner of that ledger behind it, admin or not
(ADR-0083, 2026-10-19 amendment).

**Smaller fixes, same pass.**
- The reports endpoints return a message only for a request they reject on purpose
  (`ReportRequestException`), not any exception's text.
- Rotation and post-restore reconciliation agree on what is sealed: an empty ciphertext
  counts as nothing for both.
- ASP.NET DataProtection is unused. Its keys would be unencrypted and off the data
  volume, so a guard test fails the build on the first code that reaches for it.
