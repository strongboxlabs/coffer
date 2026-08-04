# 0062 — Google Drive backup sync (off-host backup destination)

* Status: Accepted
* Date: 2026-06-24
* Amends: [ADR-0060](0060-whole-db-backup-and-admin-role.md) (completes slice ④,
  "Drive sync", deferred there)
* Related: ADR-0026 (per-ledger / master-KEK envelope encryption), ADR-0014
  (encryption at rest), ADR-0054/0055 (the scheduler the backup job runs on)

## Context

ADR-0060 ships whole-DB encrypted backups: `pg_dump -Fc` → chunked
AES-256-GCM/Argon2id `.cofferbak` artifacts, retained server-side under a tiered
GFS policy, created on demand or by the daily global `backup` job. The artifacts
live **on the Docker volume** — a single-host failure (disk loss, the box
stolen) loses them. ADR-0060 named off-host sync (slice ④) as the missing
piece and sketched it as "a refresh token sealed under the master KEK + a
scheduled push of retained artifacts."

The reference point the user raised is
[hassio-google-drive-backup](https://github.com/sabeechen/hassio-google-drive-backup):
folder-isolated Drive sync, separate local-vs-Drive retention, delete-remote-
only-once-replaced, "never delete" pins. We adopt those **behaviors**. We do
**not** adopt its default **brokered OAuth** (an author-run OAuth proxy) — Coffer
is a private, single-host, self-hosted app with no public broker and no Google
app-verification story. We use hassio's *other* path (the user's own Google
Cloud OAuth client), which is also exactly ADR-0060's "sealed refresh token."

## Decisions

### D1 — Ciphertext-only, off by default, opt-in egress

The artifact pushed to Drive is the **already-encrypted `.cofferbak`**. Google
holds ciphertext only; the passphrase and master KEK never leave the host. Drive
sync is **disabled by default** — it does nothing until an admin connects an
account. This is the only outbound-egress feature besides market-data quotes
(ADR-0054) and is documented as such.

### D2 — Auth: the user's own Google Cloud OAuth client, authorization-code redirect flow

The admin creates (or **reuses an existing**) Google Cloud OAuth client of type
**"Web application"** and gives Coffer its `client_id` + `client_secret`. Coffer
runs the standard **OAuth 2.0 authorization-code redirect flow**: `connect/start`
returns the Google consent URL (scope `drive.file`, `access_type=offline` +
`prompt=consent` to force a refresh token, an opaque CSRF `state`) → the browser
approves at Google → Google redirects to Coffer's own callback
(`/api/admin/drive-sync/oauth/callback`) with a code → Coffer exchanges it for a
**refresh token**. Coffer is already web-exposed over HTTPS (the same origin the
WebAuthn RP uses, derived from `Fido2.Origins[0]`), so it serves the callback on
its own domain — no extra infrastructure. Scope is **`drive.file`** (least
privilege — Coffer can only see/manage files it created, which dovetails with
folder isolation).

**Why this over the device-code flow (the original sketch):** the redirect flow
lets an operator **reuse a Web OAuth client they already have** — adding one
authorized redirect URI is additive and doesn't disturb that client's other
apps, so no new secret is propagated. (The user raised exactly this — "I use this
client in other apps and don't want to propagate more secrets.") The device-code
flow additionally requires a dedicated **"TVs and Limited Input devices"** client
(its own client type), which can't be reused and which Google had rejected when a
non-TV client was tried. Since Coffer has a public HTTPS origin, the redirect
flow's "needs a callback URL" requirement is free.

The callback is the one **anonymous** route on the admin surface: Google
redirects the browser to it cross-site (so it can't carry the admin cookie
reliably), and it's guarded instead by the single-use `state` minted by the
admin-only `connect/start` and delivered solely to that admin's browser via
Google — the standard OAuth CSRF mechanism.

Rejected: a Coffer-run OAuth **broker** (needs public infra + Google
verification; out of scope for a private project). Rejected for the default
path: a **service account** — personal Gmail Drives grant a service account no
storage, so it only works against a Workspace Shared Drive; the
refresh-token flow is the general-case fit. (A service-account destination can
be added later behind the same seam for Workspace users.)

### D3 — Secrets sealed under the master KEK (and rotated with it)

`client_id` + `client_secret` + `refresh_token` are serialized to a small JSON
blob and sealed with `LedgerKeyService.SealWithMasterKey` — the same primitive
the backup passphrase uses (ADR-0060) — stored as one `oauth_ciphertext`. **KEK
rotation must re-wrap it:** `KekRotationService` already re-wraps every LEK + the
backup passphrase; this blob joins that set, so `rotate-kek` keeps Drive sync
working across a rotation. (Without this, a rotation would silently break
upload — a hard requirement, not a nicety.)

### D4 — A Drive seam now; push folded into the backup flow in ④b+c

Two seams isolate the network so the connect/push flows are testable with
fakes: `IDriveOAuthClient` (the device-code dance) and `IDriveClient`
(`EnsureBackupFolder` / `Upload` / `List` / `Delete`), each with a real
`Google.Apis`-backed impl. `DriveSyncService` orchestrates connect → seal →
provision-folder, and exposes a manual `PushLatestAsync` (the **Sync now**
proof in ④a).

Folding push into the backup flow is **④b+c**: `BackupManager.CreateBackupAsync`
gains an "after store, push to each enabled destination, then reconcile that
destination's retention" step — so the daily global `backup` job *and* manual
backups sync with **no scheduler change**. That is also where the generic
`IBackupDestination` abstraction is introduced (Drive being the first impl);
until a second destination exists we do **not** introduce a generic
`backup_destinations` schema (YAGNI).

### D5 — Separate Drive retention (hassio-style), folder-isolated, safe-delete

Drive is the long-term home; local stays the rolling working set (ops.md). So
Drive keeps its **own** GFS retention (default = the same daily/weekly/monthly
tiers as local, independently configurable). Reuses `BackupStore.SelectForDeletion`
applied to the Drive listing. Borrowed from hassio:

- **Folder isolation** — Coffer manages only files inside its own Drive folder
  (created on connect; its id stored). Files moved out are ignored.
- **Per-install folders** — the `drive.file` scope hides files an install didn't
  create, so two installs on *different* clients/accounts are naturally isolated.
  But two installs that **reuse one OAuth client + account** would resolve the same
  `Coffer Backups` folder by name and commingle — and the retention sweep above
  would then prune across both (data loss). So each install generates a stable
  opaque **install id** on first connect (kept across disconnect) and names its
  folder `Coffer Backups [<install_id>]`. The id is shown in the admin UI so an
  operator can map folder ↔ install. (Added after ④a — the original D5 said
  "folder isolation" but only isolated Coffer-vs-other-files, not install-vs-install.)
- **Delete-remote-only-once-replaced** — never prune a remote artifact unless a
  newer one is confirmed uploaded (no over-deletion during a recovery).
- **Never-Delete pin** — an admin can pin a backup; pinned artifacts are
  excluded from both local and remote retention sweeps.

### D6 — Library: `Google.Apis.Drive.v3`

The official SDK (`Google.Apis.Drive.v3` + `Google.Apis.Auth`) — boring, stable,
and it handles **resumable upload** (a `.cofferbak` can exceed 100 MB) + access-
token refresh from the stored refresh token. The auth-code dance itself (build
the consent URL, exchange the code) is a couple of HTTPS calls done by hand so
the callback lands on Coffer's own route; we then hand the refresh token to the
SDK for Drive calls.

### D7 — Config storage

A deployment-wide single-row **`drive_sync`** table (mig 142): `enabled`,
`oauth_ciphertext` (D3), `folder_id` / `folder_name`, `install_id` (mig 143, the
per-install folder namespace — D5), `connected_email`,
`retention_daily/weekly/monthly`, `last_sync_at`, `last_sync_status` /
`last_sync_error`, `configured_by_user_id`, timestamps. Service-role only (no
ledger to scope it to; like `global_scheduled_jobs`).

## UI (System → Backups, admin)

A "Google Drive backups" card. **④a shipped:** **Connect** (paste the Web
client's client_id/secret → redirect to Google's consent → Google redirects back
to the Coffer callback, which seals the token + creates/locates the Coffer folder
and returns to System → Backups with a `?drive=connected|denied|error` result the
card surfaces), then status (connected account, folder, last upload) with
**Disconnect**.

**④b+c adds (shipped):** an **"Automatically upload each new backup to Google
Drive"** toggle, the **Drive-retention** editor (daily/weekly/monthly), a single
**"Upload all backups now"** button (uploads every local backup not yet on
Drive), a **"Not uploaded recently"** staleness badge, and a per-row **Pin /
Unpin** ("never delete") control in the backups list. (The feature is one-way
**upload** — the UI deliberately avoids "sync".)

## Consequences

- Off-host DR: a host loss no longer loses backups. Restore still flows through
  the existing CLI / bootstrap-restore once an artifact is back on a host.
- New outbound egress to `googleapis.com` (opt-in, documented). Ciphertext only.
- A real external dependency + a one-time GCP-project setup by the operator
  (documented in operations.md). Degrades gracefully — disabled = no-op; a sync
  failure records `last_sync_error` and never fails the backup itself.
- KEK rotation now also re-wraps the Drive OAuth blob (D3).

## Slices

- **④a** *(this slice)* — `drive_sync` table (mig 142); the `IDriveOAuthClient`
  / `IDriveClient` seams + `Google.Apis` impls; `DriveSyncService` (connect-start
  / oauth-callback / disconnect / push-latest) sealed under the master KEK (+
  KEK-rotation re-wrap); the admin `/api/admin/drive-sync` surface (RequireAdmin,
  plus the anonymous state-guarded OAuth callback); the Connect card with
  **Sync now** / **Disconnect**. Ships: an admin can connect Drive (reusing a
  Web OAuth client) and a manual push of the latest backup works. The auto-push
  toggle + remote retention are intentionally **not** here (they'd be inert until
  ④b+c). *Auth pivoted from the originally-sketched device-code flow to the
  authorization-code redirect flow so an existing Web client can be reused — see
  D2.*
- **④b+c** *(shipped)* — the `IBackupDestination` seam + `GoogleDriveBackupDestination`;
  `BackupManager` pushes to every enabled destination after each backup (daily +
  manual) — a push failure never fails the backup; remote GFS retention reconcile
  (delete-only-once-replaced, never the newest); **Never-Delete pins** (mig 144
  `backup_pins`, excluded from local + remote sweeps); **Upload existing** backfill;
  the enable toggle + retention editor + staleness badge; and `pg_dump --compress=zstd`
  by default (`Api:Backup:Compress`, ~10% smaller than zlib). **The dump excludes
  `ledger_snapshots` data** (`--exclude-table-data`, added 0.8.x): the ADR-0037 in-place
  snapshots are *local* restore points, not DR payload, and as already-compressed blobs
  they pass through the dump ~1:1 — up to 5 full copies of the ledger were ballooning
  every backup (a ~141 MB dump dropped to ~22 MB once excluded). The table *schema* stays
  in the dump, so a restored DB has an empty `ledger_snapshots` and new snapshots
  regenerate. Follow-up (0.4.1):
  Drive files carry the `.cofferbak` extension, the manual control is one
  "Upload all backups now", and the UI says "upload" not "sync".
