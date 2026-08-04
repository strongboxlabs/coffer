# 0083 — Multi-user: role enforcement, per-ledger sharing, invite links, ledger lifecycle

* Status: Accepted (slice A backend shipped; slice A UI + slice B invites to follow)
* Date: 2026-07-23
* Related: ADR-0013 (WebAuthn auth + bootstrap ceremony — invites generalize its
  token), ADR-0020 (multi-ledger RLS + the owner/editor/viewer role matrix this
  finally enforces), ADR-0026 (ledger = crypto boundary; the LEK is wrapped by the
  deployment KEK, so sharing needs no per-user key exchange), ADR-0032 (triggers are
  a last resort — the ≥1-owner invariant stays API-side), ADR-0060 (admin role),
  ADR-0071 (a new ledger is owned by its creator)

## Context

The permission *model* already exists: `user_ledger_grants(user_id, ledger_id, role)`
with `role ∈ {owner, editor, viewer}` (ADR-0020), RLS filtering every domain table by
the caller's grants, and an `is_admin` operator flag (ADR-0060). But it is unmanaged
and under-enforced:

- **Role is not enforced.** RLS policies are `FOR ALL` on presence-of-grant, so *any*
  grant = full read+write — a `viewer` can write today. No API role check exists
  (nothing needed one yet). This is a latent security gap, not just a missing feature.
- **No management surface.** No way to list / invite / disable users, or to see /
  change / remove who has access to a ledger.
- **No invite path.** The only user-creation route is the bootstrap token, hardwired
  to the first user (it mints only while zero credentials exist).
- The "≥1 owner" invariant's DB trigger was dropped (ADR-0032) in favor of API-side
  enforcement that was never built.

The owner wants real multi-user: invite people (e.g. a spouse) to specific ledgers
with a role, manage users, and manage the ledgers themselves.

## Decisions

### D1 — Invite-only, two authority levels
No open registration; the only way in is an invite (or the first-user bootstrap).
- **Instance admin** (`is_admin`) — the operator: manages all users, issues instance
  invites, can grant/revoke admin.
- **Ledger owner** (grant `role='owner'`) — manages their ledger: shares it, changes
  members' roles, renames/deletes it, transfers ownership.

### D2 — Roles enforced in BOTH the API and RLS (defense in depth)
- **API**: a `LedgerAuthorizer` resolves the caller's role on the target ledger. Writes
  require `editor`|`owner`; share / rename / delete / transfer require `owner`; a
  `viewer` gets 403 on any mutation.
- **RLS (DB backstop)**: migrations 071/072 already flattened the child tables to filter
  by `ledger_id` *directly* (planner speed), so there is **no FK-composition backstop** —
  every ledger-scoped, `coffer_app`-writable table (~30) must be covered. Per table,
  replace the single `FOR ALL` `<t>_per_user` policy with two: `<t>_read`
  (`FOR SELECT USING (ledger_id IN <any-grant>)`) and `<t>_write`
  (`FOR ALL USING (ledger_id IN <owner/editor grant>) WITH CHECK (<same>)`). Postgres
  OR-combines permissive policies, so SELECT passes via `_read` (viewers see data) while
  INSERT/UPDATE/DELETE satisfy only `_write` (owner/editor). Applied uniformly by a
  PL/pgSQL loop over the table list, reusing the current inlined-subquery predicate shape
  for planner parity. Identity/self tables (`users`, `auth_*`, `webauthn_*`,
  `recovery_codes`, `user_preferences`, `user_account_groups`) are user-scoped, not
  ledger-role-scoped, and are untouched; `ledgers` + `user_ledger_grants` stay
  SELECT-only for `coffer_app` (grant/lifecycle writes go through the service role, gated
  by the API authority checks). A missing API check can never grant a viewer a write.

### D3 — Invites are a generalized, scoped bootstrap token
New table `invites`: `id`, `token_hash` (SHA-256; plaintext shown once), `issued_by_user_id`,
`ledger_id` (nullable — null = instance-only), `role` (nullable grant role),
`grants_admin` (bool), `expires_at` (default +7d), `consumed_at`, `created_at`.
Single-use, expiring, rate-limited on redeem.
- **Issue**: owner → `POST /api/ledgers/{id}/invites {role}`; admin →
  `POST /api/admin/invites {ledgerId?, role?, grantsAdmin?}`.
- **Redeem** (`/invite/{token}`): a new person runs the WebAuthn registration ceremony,
  reusing the ADR-0013 bootstrap machinery (user + credential + 10 recovery codes + the
  invite's grant, one service-role transaction, token consumed); an already-signed-in
  user hits `POST /api/invites/{token}/accept` to apply the grant. Because the LEK is
  wrapped by the deployment KEK (ADR-0026), a grant row alone gives full ledger access —
  no per-user key exchange.
- **Manage**: a pending (unconsumed, unexpired) invite is **listable + revocable** by its
  public `id` (never the token hash) — an owner over their own ledger's invites
  (`GET`/`DELETE /api/ledgers/{id}/invites[/{inviteId}]`), an admin over all
  (`GET`/`DELETE /api/admin/invites[/{inviteId}]`). Revoke hard-deletes the row (an unused
  handle, no audit value). Expiry (default 7d) is the passive backstop; revoke is the
  active kill switch for a link sent to the wrong place.
- **Delivery & trust — no email/OTP.** Coffer has no email subsystem and accounts carry
  no email address (passkey-only auth, ADR-0013), so there is no activation email or
  one-time code. The link's assurance is the high-entropy single-use token + expiry +
  revoke + the *mandatory* passkey registration; the issuer delivers it out-of-band to a
  known person, exactly as the operator receives the first-run bootstrap URL. Email-based
  verification (an email-address column + an SMTP/mail dependency + a verification
  round-trip) is a deliberate future decision if invites ever need to survive a
  less-trusted channel — explicitly out of scope here.

### D4 — Invariants enforced API-side (ADR-0032), self-action guarded
- **≥1 HUMAN owner per ledger**: demoting / removing the last human owner is rejected.
  The synthetic system service identity holds an owner grant on every ledger (for the
  service-role flows — importer, bootstrap) but is **hidden from the Members surface and
  cannot be changed or removed**, and its grant does NOT satisfy this invariant — so a
  ledger always keeps a real human owner, never just the service account.
- **≥1 admin instance-wide**: demoting / disabling / deleting the last admin is rejected.
- **No self-lockout**: you can't demote / disable / remove yourself as the last
  owner/admin.
- **Disable** (`is_disabled`) is the reversible primary action (keeps grants, blocks
  login). Hard-delete is admin-only and blocked while the user is any ledger's last
  owner (reassign first).
- `is_admin` and grant rows are written via the **service role** (mig-138 already
  restricts `is_admin`; `user_ledger_grants` is SELECT-only for `coffer_app`), gated by
  the API authority checks — the established ledger-create escalation pattern.

### D5 — Ledger lifecycle
`POST /api/ledgers {name}` create (caller becomes owner; seeds starter categories,
ADR-0071 D5); `PATCH /api/ledgers/{id} {name}` rename (owner); `DELETE /api/ledgers/{id}`
delete (owner; typed-confirm, cascades). Ownership transfer = promote another member to
owner (± demote self) via the members endpoint under the ≥1-owner guard — no separate
transfer endpoint. Wires the existing General-settings rename/delete stubs.

## UI flow

- **System → Users** (admin-only tab): user table (name, username, status, admin, ledger
  count); "Invite user" → dialog (instance-only, or initial ledger+role, or grant admin)
  → one-time link + Copy; row actions Disable/Enable, Make/Remove admin.
- **Ledger → Settings → Members** (owner-gated mutations; members see it read-only):
  member table with a per-row role dropdown + Remove; "Invite to this ledger [role]" →
  one-time link; the last-owner row is locked with an explanatory tooltip.
- **`/invite/{token}`** (anonymous): "You've been invited to *[Ledger]* as *[role]*";
  not-signed-in → passkey registration → account + grant + recovery codes; signed-in →
  Accept. Invalid / expired / consumed → a clear error state.
- **Ledger lifecycle**: "+ New ledger" in the ledger switcher; rename / delete wired into
  General settings (owner; typed-confirm on delete).

## Slices

- **A — Enforce roles + manage what exists.** `LedgerAuthorizer` + role-aware RLS write
  policies (migration on the anchor tables) + admin Users list (view, disable/enable) +
  ledger Members panel (list, change role, remove; ≥1-owner guard) + ledger
  create/rename/delete. Makes today's latent multi-user safe and manageable; ships the
  security fix.
- **B — Invites & onboarding.** `invites` table + issue (owner/admin) + redeem (register /
  accept) + the `/invite/{token}` page + admin invite + admin promote/demote. The "bring
  people in" story.
- **Deferred**: SSE live-edit notifications across users on one ledger (follow-ups, Phase 5+).

## Consequences

- The dormant owner/editor/viewer model (ADR-0020) becomes real and enforced at both
  layers; a viewer can no longer write — a latent security gap closes.
- Coffer becomes genuinely multi-user (households) while staying invite-only and
  single-instance. RLS remains the cross-user boundary and is now exercised by more than
  one user — the follow-ups' "RLS is untested (tests bypass it via the service role)"
  note gets addressed by multi-user integration tests that run under `coffer_app`.
- The bootstrap token generalizes into invites; the first-user ceremony is unchanged.
- New surfaces are admin- or owner-gated at the API (the real boundary); the UI gates are
  cosmetic (ADR-0060 pattern).
