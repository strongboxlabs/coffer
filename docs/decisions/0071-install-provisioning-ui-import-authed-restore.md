# 0071 — Install provisioning, UI-driven Moneydance import, and authenticated restore

Status: Accepted — **D1 superseded by [ADR-0088](0088-setup-asks-one-question.md)**
Date: 2026-07-02
Amends: [ADR-0060](0060-whole-db-backup-and-admin-role.md), [ADR-0061](0061-bootstrap-restore.md)
Relates: [ADR-0013](0013-webauthn-passkey-auth.md), [ADR-0015](0015-importer-hosting-model.md), [ADR-0052](0052-moneydance-reimport-idempotency.md)

> **Superseded in part (2026-08-02).** D1's `provision --mode {clean|demo}`
> subcommand and `scripts/provision.sh` are **retired**, and the placeholder
> Default/Demo ledger rows this ADR relied on are **dropped by migration 186**.
> D1 assumed the CLI would always run before the first user; it never did on the
> documented dev path, so fresh installs kept two empty ledgers and the setup
> picker offered them as if they held data. Install shape is now a single
> checkbox on the setup form — see
> [ADR-0088](0088-setup-asks-one-question.md). D2 (UI-driven import), D3/D4
> (authenticated restore) and D5 (starter categories, now scoped to ledgers
> created empty) stand.

## Context

Three gaps in the "stand up / populate / recover" story:

1. **Fresh install shape.** Migrations seed a Default ledger (…0001) *and* a Demo
   ledger (…0002), both empty, on every install; the demo *dataset*
   (`data/samples/moneydance-export-demo.json`) is never loaded. An empty Demo has
   no value, and an empty Default is questionable. We want a fresh install to come
   up either **clean (no ledgers)** or **demo (a seeded Demo ledger)**, via a
   simple script.
2. **Moneydance import is CLI-only** (`coffer-import-moneydance`). It should be an
   in-app action so a user can create a ledger from an MD export without the CLI.
3. **Restore is bootstrap-only** (pre-auth) or CLI. An admin running the app has no
   in-UI way to restore — the exact deferred item ADR-0060 called out. This is also
   the path to **migrate from another install**.

An audit confirmed a **zero-ledger install is safe**: the API and SPA already
handle an empty ledger list ([LandingPage.tsx](../../src/Web/src/routes/landing/LandingPage.tsx)
empty state; the setup ceremony's "create new ledger" path works with zero
existing ledgers). The only hard dependency on the Default *row* is the importer
CLI's no-args fallback ([LedgersRepository.cs:112](../../src/Importer.Moneydance/Db/LedgersRepository.cs)),
which our UI import never uses (it always names a new ledger). The **system user
row (…0001) must stay** — the importer's owner-grant FK depends on it.

## Decisions

### D1 — Install provisioning: `clean` | `demo`

A `coffer-api provision --mode {clean|demo}` subcommand (project stack / EF, not
raw SQL in a shell), **gated on "no human users yet"** so it can only reshape a
pre-first-user database:

- **clean:** delete the empty Default (…0001) and Demo (…0002) ledger rows and
  their grants → **zero ledgers**. Keep the system user row. The first ledger then
  comes from the `/setup` ceremony or an MD import.
- **demo:** seed the Demo ledger (…0002) with the bundled dataset via the **same
  import engine as D2**, and have setup-complete **auto-grant the first user**
  owner access to Demo.

`scripts/provision.sh --mode {clean|demo}` wraps `docker compose up`, waits for
readiness, runs the subcommand, and prints the `/setup` URL (the first-admin
passkey step stays a browser action — the script does not automate it).

Provisioning is idempotent and a no-op once any human user exists (protects real
installs). It is **not** a migration — migrations still create Default/Demo; the
subcommand removes/repopulates them on a fresh DB only.

### D2 — UI-driven Moneydance import (new ledger, any user)

Extract an `IMoneydanceImportService` from the CLI command. The pipeline steps,
mappers, and Dapper repos are already decoupled; only the Spectre.Console command
wrapper is removed. The service takes an **owner userId** (the importing user for
UI imports; the system user for the CLI and the demo seed) instead of hardcoding
the system user.

Exposed to **any authenticated user** (not admin-only) as **new-ledger-only**
(which also satisfies the ADR-0052 seed-once guard for free):

- `POST /api/imports/moneydance/preview` — multipart upload; parses + dry-runs;
  returns per-type counts (accounts/txns/securities/…) and validation warnings.
  No DB writes.
- `POST /api/imports/moneydance` — creates the named new ledger owned by the
  caller, enqueues a **background job**, returns a job id.
- `GET /api/imports/moneydance/{jobId}` — job status/progress for polling.

Long imports (108k+ inserts, 10-min command timeout) run in the background, not a
request. Upload ceiling mirrors restore (~128 MB); the CLI stays the escape hatch
for larger exports.

### D3 — Authenticated admin restore

`POST /api/admin/backups/restore` (RequireAdmin), reusing ADR-0061's **stage → restart →
apply-at-boot** machinery (`BootstrapRestoreStaging` + the boot block in
Program.cs) — no new restore engine.

- **Upload is the primary source** (works without Google Drive; it is the
  cross-install migration path). **Secondary:** restore a backup already listed in
  `BackupsPanel` (local or Drive).
- Pre-flight at accept, before any restart: (a) verify the passphrase opens the
  archive; (b) the **D4 KEK fingerprint** check.
- A **type-to-confirm** gate with a blunt statement — replaces **all users, all
  ledgers, all data**; **everyone is signed out** — requiring the admin to type an
  exact phrase (e.g. `yes i agree`).
- On apply, the current session (and all sessions) are gone with the old DB; the
  SPA shows the "restoring…" screen (reused from bootstrap restore), polls, and
  lands on `/login` — or `/setup` if the restored backup predates any credential.

### D4 — KEK fingerprint for cross-install / rotation safety

A restored DB carries secrets sealed under the *source* install's Master KEK (the
stored backup passphrase and the Drive OAuth token). On a different KEK they will
not unseal.

- **Backup create** writes a **KEK fingerprint** — a *derived* value
  (`HKDF(KEK, "coffer-kek-fingerprint")`, truncated), **never the KEK itself and
  not a raw hash of it** — into the **cleartext, versioned header** of the
  `.cofferbak` (next to the Argon2 salt), so it is checkable without the
  passphrase. If the existing KEK-rotation code already carries a key-id/version,
  reuse that instead of a parallel fingerprint.
- **Restore accept** compares the header fingerprint to the target install's KEK:
  - **Match** → proceed; sealed secrets survive (clean DR/migration).
  - **Mismatch** → warn ("sealed under a different Master KEK; set
    `COFFER_MASTER_KEK_BASE64` to the source's and re-upload for a clean migration,
    or proceed and re-set the backup passphrase + reconnect Drive afterward") and
    require a **second explicit acknowledgement** on top of the type-to-confirm.
    Not a hard block — the data and passkeys restore either way.
  - **Legacy backup (no fingerprint field)** → cannot verify; show the generic
    caveat. The header is versioned so old `.cofferbak` files still restore.
- **Hard rule:** the KEK is never placed in the backup. A backup + passphrase
  already unlocks the *data*; carrying the KEK would also unseal the operational
  secrets. So mismatch can only warn/instruct — never auto-provision the source KEK.
- Correctly flags restoring a **pre-rotation** backup on the same install (its
  inner secrets genuinely won't unseal under the rotated KEK).

### D5 — Starter categories for new ledgers

An empty ledger can't categorize anything, and D1's clean install makes the
empty-new-ledger path the default. So creating a ledger seeds a **starter
category set** — a curated, general-purpose income/expense tree shipped as a
reviewed canonical file under `src/Api` (authored from the sample ledger, *not*
read from the demo at runtime — so it's decoupled from the demo-dataset
embedding and "designed for general release").

- **Applied on the empty-ledger paths:** `POST /api/ledgers` and setup's
  create-first-ledger, as category-type accounts (`account_type='category'` +
  `category_kind`) stamped with the new ledger id, reusing the existing
  category-creation code path. **Not** the import paths (MD/OFX bring their own
  categories) and **not** demo mode (the demo import seeds them).
- **Categories only** — no default accounts; bank/card accounts are personal,
  the user adds their own.
- **Opt-in, default on:** a "Start with default categories" toggle on the New
  Ledger dialog lets power users start blank.

## UI walkthrough

**Setup (`/setup/{token}`)** — unchanged mechanics; the *state* differs by mode.
- *clean install:* zero ledgers → the ceremony shows "create your first ledger"
  (name input); no picker.
- *demo install:* Demo is present → shown in the picker; setup-complete auto-grants
  the first user owner of Demo. They may still create their own.

**Import wizard (in-app, any user)** — entry: "New ledger from Moneydance" on the
ledger hub / landing.
1. **Upload** — pick the MD export `.json`.
2. **Preview** — parsed per-type counts + warnings (from `/preview`); enter the new
   ledger's name.
3. **Confirm** — starts the background job; a progress screen shows step/percentage
   (polling `/{jobId}`).
4. **Done** — open the new ledger. Failure → error with the validation report; no
   partial ledger (import is transactional).

**Restore (admin, System → Backups)** — a "Restore" section below backups.
1. **Source** — primary: upload `.cofferbak` + passphrase. Secondary: pick a listed
   local/Drive backup + passphrase.
2. **Checks** — passphrase verified; KEK fingerprint compared. Mismatch → inline
   warning + the extra acknowledgement (D4).
3. **Confirm** — red, blunt type-to-confirm gate (D3).
4. **Apply** — "restoring…" screen (reused), poll, land on `/login` (or `/setup`).

## Build order (slices)

1. **Slice A** — extract `IMoneydanceImportService` (pure refactor + tests; no
   behavior change; owner-userId parameter).
2. **Slice B** — UI import: preview/import/status endpoints, background job runner,
   the wizard.
3. **Slice C** — `provision` subcommand + `scripts/provision.sh` (demo mode reuses
   the Slice-A service; clean mode is the ledger-row cleanup).
4. **Slice D** — KEK fingerprint in the backup header (write on create, verify on
   restore) — small, foundational for E.
5. **Slice E** — authenticated admin restore: endpoint, staging reuse, KEK
   pre-flight, typed confirmation UI.

## Consequences

- The importer CLI's no-args fallback assumes the Default row; on a `clean` install
  that row is gone, so CLI users must pass `--ledger-name`. Update the CLI's
  fallback error to say so (it already errors clearly).
- Import becomes a per-user, resource-bearing operation (any user can start a large
  background job); a concurrency/size guard keeps one install honest. (TBD: exact
  limits.)
- Authed restore adds a second, admin-gated entry to the destructive restore path;
  ADR-0060/0061's stage→restart→apply invariant is preserved (not a new engine).
- The `.cofferbak` header gains a versioned KEK-fingerprint field; readers must
  tolerate its absence (legacy) — a format version bump, backward compatible.

## Resolved defaults

- **Import limits:** one running import per user at a time; ~128 MB upload cap
  (mirrors restore). Larger exports use the CLI.
- **Demo grant:** `demo` mode auto-grants the first user **owner** of the Demo
  ledger (it's their install).

## Open items / TBD

- Reconcile D4 with the existing KEK-rotation key-id, if one exists (reuse vs new)
  — resolved during Slice D by reading the rotation code.
