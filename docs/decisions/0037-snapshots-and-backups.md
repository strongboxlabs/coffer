# 0037 — Snapshots and backups

* Status: Accepted
* Date: 2026-06-08
* Related: [ADR-0014](0014-encryption-at-rest.md) (per-ledger LEK, master KEK), [ADR-0031](0031-ingest-provider-pattern.md) (the JSON-walker pattern this reuses for restore)

## Context

Coffer stores the user's complete financial history. Three real failure modes the user has flagged:

1. **Disaster recovery.** Self-hosted Postgres data lost (disk failure, accidental `DROP`, container blown away). Without an off-host backup, the ledger is gone — the Moneydance JSON the user bootstrapped from is now months stale.
2. **Pre-risk safety net.** "I'm about to do a giant import / a destructive operation / a schema migration — let me grab a snapshot first so I can roll back if it goes sideways."
3. **Data portability.** "I want my data in a format I can read / archive / move outside Coffer." Matches the user-bound-IO posture (memory: "import/export/backup/restore are user-bound API endpoints").

A single mechanism can't serve all three well — DR wants off-host encrypted blobs, pre-risk wants cheap server-side checkpoints with one-click rollback, portability wants a readable file format. We split them into two tracks with shared internals.

## Decision

Two distinct user-facing concepts, sharing the same JSON-walker engine:

### Snapshots — operational, server-side, capped

* **Purpose:** pre-risk safety net + lightweight rollback. Never leaves the host.
* **Schedule:** opt-in **daily** auto per ledger (a `snapshot` row in `scheduled_jobs`, mig 136), run by the shared `SchedulerService`, plus user-initiated. (Superseded the original fixed-weekly design, which was a **no-op**: the worker ran under the RLS app role with no request user and saw no ledgers — zero auto-snapshots in practice. The shared worker uses the service role.)
* **Retention:** hard cap of 5 total per ledger. Auto-snaps evict the oldest auto-snap on creation; user-initiated ("manual") snaps survive until the user explicitly deletes one. If all 5 slots hold manual snaps, the auto-snap is **skipped** (logged + surfaced on the snapshots panel — "Auto-snap skipped: 5 manual snapshots in pool"); manual creation at the cap is **refused** with a clear error ("delete a snapshot first").
* **Scope (in):** user-curated ledger graph — `ledgers`, `accounts`, `account_groups`, `account_external_ids`, `securities`, `security_prices`, `holdings`, `lots`, `txn_headers`, `txn_legs`, `txn_header_overrides`, `txn_leg_overrides`, `tags`, `txn_header_tags`, `provider_security_mappings`.
* **Scope (out):** operational state — `feed_connections` and their credential ciphertexts, `sync_runs`, `sync_run_errors`, `sync_run_promotions`, `auth_sessions`, `bootstrap_tokens`, `schema_migrations`. Also out: `txn_header_account_balances` (the materialized balance table from mig 089) — re-derived on restore via `fn_recompute_balances_for_account` for each restored account, avoiding stale-balance risk.
* **Restore:** in-place replace on the same ledger. Wrapped in one Postgres transaction (`BEGIN` → delete the ledger's current rows in the in-scope tables → bulk-INSERT from snapshot payload → recompute balances → `COMMIT`). If anything fails, the original ledger state is intact; no half-restored visibility.
* **Schema-version compatibility (Phase 1):** refuse restore when the snapshot's `schema_version` differs from the live DB. Clear error message. Forward-migration of older snapshots is a separate ADR.
* **Failure handling for auto-snap:** the shared scheduler logs a per-ledger failure and continues; `next_run_at` still advances, so the next day's run starts fresh. No retry within a tick.

### Backups — disaster recovery, off-host, encrypted

* **Purpose:** 100% recovery from total meltdown (new machine, fresh Coffer install) in under 5 minutes once the backup file is in hand.
* **Schedule (Phase 1):** **manual only**. User clicks "Download backup" → server walks the ledger → SPA streams the encrypted file. Scheduled backups (to Google Drive on a cadence) are deferred to a follow-up.
* **Destination:** local file download in Phase 1. Google Drive integration deferred — same backup engine, different sink.
* **Scope:** **all-inclusive minus identity.** Includes everything snapshots include, plus the operational/secret state (feed_connections + their ciphertexts, the wrapped LEK so encrypted-at-rest columns survive). Excludes users, WebAuthn credentials, sessions — identity is device-tied and is re-established on the new install before the user clicks Restore. The freshly-created setup user becomes the owner of the restored data.
* **Encryption:** passphrase-derived key (Argon2id) wrapping AES-256-GCM over a gzip-compressed JSON payload. The passphrase is user-controlled and is the SINGLE recovery-critical secret. Drive is treated as untrusted storage — the file is unreadable without the passphrase. Backup envelope:

  ```
  {
    "format": "coffer-backup-v1",
    "schemaVersion": "<live DB schema version>",
    "createdAt": "<UTC iso>",
    "ledgerId": "<uuid>",
    "kdf": {
      "algorithm": "argon2id",
      "salt": "<base64 random>",
      "params": { "memoryKib": 65536, "iterations": 3, "parallelism": 1 }
    },
    "cipher": {
      "algorithm": "AES-256-GCM",
      "nonce": "<base64>",
      "ciphertext": "<base64 of gzip(JSON of full backup payload)>"
    }
  }
  ```

* **Restore flow (post-meltdown):**

  1. Fresh Coffer install on new machine.
  2. Setup screen creates a new user (WebAuthn enrollment).
  3. User signs in → empty Ledger Hub.
  4. Click **Restore from backup** → upload `.cofferbak` file → enter passphrase.
  5. Server validates schema version, decrypts, deserializes, runs a transactional bulk-restore (same atomicity contract as snapshot restore).
  6. The restored wrapped LEK is re-wrapped under the new install's master KEK so encrypted-at-rest columns are readable on the new host.
  7. User is now operating on the restored ledger; feed connections work (their encrypted credentials survived); transactions are visible.

* **Schema-version compatibility (Phase 1):** same posture as snapshots — refuse on mismatch, forward-migration is its own ADR.

### Shared internals

The same JSON-walker drives both tracks. Schema:

```sql
CREATE TABLE ledger_snapshots (
    id                   uuid       PRIMARY KEY,
    ledger_id            uuid       NOT NULL REFERENCES ledgers(id) ON DELETE CASCADE,
    created_at           timestamptz NOT NULL DEFAULT now(),
    created_by_user_id   uuid       NOT NULL REFERENCES users(id),
    kind                 text       NOT NULL CHECK (kind IN ('auto', 'manual')),
    description          text,                    -- manual snaps carry a free-form note
    schema_version       text       NOT NULL,
    content              bytea      NOT NULL,     -- gzip-compressed JSON of the in-scope graph
    content_size_uncompressed integer NOT NULL    -- for display ("47 MB before compression")
);
CREATE INDEX idx_ledger_snapshots_ledger_created
    ON ledger_snapshots (ledger_id, created_at DESC);
```

The 5-cap eviction rule lives in `LedgerSnapshotsRepository` (auto-evict-auto-first is non-trivial enough that a DB constraint would be uglier than the LINQ).

Backups don't get a DB table — they're streamed-on-demand artifacts that exist only as files outside the system.

### UI surface

Two new settings routes — both don't exist today; backups + snapshots are the first inhabitants of each:

* `/ledgers/:lid/settings` — per-ledger settings. Snapshots panel (list + create + restore + delete), backup download. Future neighbors: rename ledger, delete ledger, ledger-level preferences.
* `/settings` (or `/account`) — user-level settings. Future home for Drive auth (when scheduled backups land), WebAuthn device management, display name. Empty in Phase 1 — but we put the route in place so the pattern is established.

Snapshots/backup work belongs in **per-ledger** settings because they're per-ledger artifacts. The Drive-auth piece (when it lands) belongs in **user-level** settings because the user authorizes Drive once for their account, not once per ledger.

## Locked design decisions

1. **Two tracks, not one.** Snapshots and backups stay separate concepts with separate retention, separate UI surfaces, separate audiences. Don't collapse them.
2. **Identity stays out of backups.** WebAuthn credentials are device-tied. The freshly-created post-meltdown user owns the restored data.
3. **Passphrase-derived encryption for backups.** Drive (or any off-host destination) is untrusted storage. The passphrase is the one thing the user must safeguard separately.
4. **Manual-at-cap refuses, doesn't evict.** Manual snaps are explicit intent; silent eviction would surprise the user.
5. **Re-derive balances on restore.** Don't back up `txn_header_account_balances`; rebuild via `fn_recompute_balances_for_account` after the row insert.
6. **Phase 1 = refuse cross-version restore.** Forward-migration of older payloads is a separate problem deserving its own ADR; deferring keeps Phase 1 scope honest.
7. **Phase 1 = manual backups only.** No scheduled Drive backups in v1. Same backup engine; scheduling + Drive auth land later.

## Out of scope (deferred)

* **Google Drive integration** — OAuth, token storage, upload/download, scheduling. Phase 2 of the backups track.
* **Forward-migration on restore** — own ADR. Needed when schema drift between backup and live DB becomes a real pain point.
* **Merge-into-existing-ledger restore** — never going to ship; merge semantics for a backup are a swamp.
* **Cross-ledger / whole-system backup** — operator concern (the sysadmin runs `pg_dump`); Coffer's surface stays user-bound per ledger.
* **Backup file portability between Coffer versions** — implies forward-migration (above) and a backwards-compatibility contract we're not ready to commit to.
* **Snapshot diff view** ("show me what changed since this snapshot") — would be useful but not in scope. Restore-or-don't is the v1 affordance.

## Why this shape

* **One mechanism couldn't serve all three failure modes.** DR needs off-host + encrypted, pre-risk wants in-DB + one-click, portability wants readable. Two tracks with shared internals threads all three without bloating either surface.
* **The passphrase-derived key for backups is the only design that holds.** Master-KEK-in-the-backup means the file IS the keys (lose it = lose all secrets). No-secrets-in-backup means post-restore the user re-OAuths every feed, breaking the 5-minute recovery target. Passphrase is the right hop: one human-rememberable secret, off-host storage is untrusted by default, recovery from total loss is bounded by network speed.
* **Identity out of scope keeps restore simple.** Trying to back up WebAuthn credentials introduces real complexity (device-tied state, authenticator availability) for no real benefit on a new machine.
* **Re-derive balances on restore** is structurally cleaner than backing them up. The materialized balance table is derivable from `txn_legs`; backing it up risks divergence between the materialised values and the underlying legs (a class of bug we've already seen from triggers).
* **5-cap with auto-evicts-auto-first** matches the user's mental model: "the safety snapshot I made is the important one; system housekeeping shouldn't quietly delete it."
