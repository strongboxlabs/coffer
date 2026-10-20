# 0060 — Whole-DB encrypted backup, admin role, Drive-later

Status: Accepted — the operator CLI
(`coffer-api restore`) it introduced was removed by
[ADR-0094](0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md);
restore is UI-only. Backup creation via the CLI is unchanged.

## Context

Snapshots (ADR-0037) own per-ledger, in-place recovery — but they live *inside*
the Postgres they protect, are plaintext, and can't leave the host, so they
don't cover the disaster case: total DB/host loss, or moving to a new install.
ADR-0037 framed the off-host "backup" track as **per-ledger** and scoped whole-
system backup out as an "operator concern." In practice the operator of a self-
hosted Coffer *is* the user, per-ledger recovery is already solved by snapshots,
and a one-file "everything" backup is both simpler and the truer DR story. This
ADR **reframes the backup track to whole-database** (ADR-0037's per-ledger
backup design is superseded; its snapshot design stands unchanged).

A whole-DB backup crosses every user and ledger, so it can't be a per-user
action — it needs an **admin role** to gate it. The user wants it surfaced like
the Snapshots panel and, eventually, synced to Google Drive.

## Decision

**Admin role.** New `users.is_admin` (migration 138). The first human user
becomes admin at setup-complete (robustly: admin iff no admin exists yet); the
system service identity is never admin. Distinct from the per-ledger
`user_ledger_grants`. `/api/auth/me` surfaces `isAdmin`. The `RequireAdmin`
policy gates the admin endpoints; the auth handlers stamp an `is_admin` claim —
the cookie handler reads it in the session-validation query (joined to `users`,
one round-trip), and dev-auth stamps it true (the local operator). The
privilege boundary that only the service role may *set* `is_admin` is migration
138's column grant.

**Backup engine.** `pg_dump --format=custom --no-owner` (as `coffer_service`,
which reads all rows; privileges are kept so restored GRANTs come back) →
**chunked AES-256-GCM** (per-chunk nonce + AAD framing so an arbitrarily large
dump never buffers wholly in memory) keyed by **Argon2id(passphrase)**
(`Konscious.Security.Cryptography.Argon2`; ADR-0037 KDF params). The Docker
runtime stage gains `postgresql-client-16`.

**Passphrase model.** *One* admin-set backup passphrase, **sealed under the
master KEK** (`LedgerKeyService.SealWithMasterKey`, AES-GCM; it isn't owned by
any ledger) and stored in `global_scheduled_jobs.passphrase_ciphertext` — never
in cleartext. Both manual and scheduled backups encrypt under it, so there is a
**single restore secret**. The KEK lives only in `COFFER_MASTER_KEK_BASE64`
(env), so a stolen artifact *or* a stolen DB row is inert without it. Rotating
the passphrase re-seals it for *future* backups; existing artifacts still need
the previous passphrase (called out in the UI).

**Schedule storage.** The whole-DB backup has no owning ledger, so it can't live
in the per-ledger `scheduled_jobs` (PK `(ledger_id, job_type)`, NOT NULL FK).
Migration 139 adds a sibling **`global_scheduled_jobs`** (PK `job_type`),
service-role-only (RLS deny-all, like `bootstrap_tokens`), carrying the schedule
*and* the sealed passphrase. The single `SchedulerService` scans **both** tables
each tick — per-ledger via `IScheduledJobHandler`, global via
`IGlobalScheduledJobHandler` — so it stays one worker, two job scopes (not a
parallel scheduler).

**Retention.** Backup artifacts are **retained server-side, encrypted**, under
`data/backups/*.cofferbak` (the Docker volume), pruned after each create by a
**tiered grandfather-father-son** policy: keep every backup for the last
`RetentionDailyDays` days (default 7), then the newest of each ISO week for
`RetentionWeeklyWeeks` weeks (default 8), then the newest of each month for
`RetentionMonthlyMonths` months (default 12); older than that is dropped. The
selection (`BackupStore.SelectForDeletion`) is pure + clock-injected so the
bucketing is unit-tested without disk. Filenames are millisecond-stamped so the
just-written artifact sorts unambiguously newest; ids are validated against a
strict pattern + confirmed inside the directory (no traversal). On-box is a
rolling working set; downloads (and, later, Drive) are the long-term archive.
Server-side retention is the prerequisite for Drive sync.

**Endpoints (admin-gated).** `POST /api/admin/backups` (create now),
`GET /api/admin/backups` (list), `GET /api/admin/backups/{id}` (download the
`.cofferbak`), `DELETE /api/admin/backups/{id}`, `PUT /api/admin/backups/passphrase`,
`GET|PUT /api/admin/backups/schedule`. Create + schedule-enable both 422
(`backup-passphrase-not-set`) until a passphrase exists.

**IA + UI.** Deployment-wide settings live on a **System** page (`/system`),
reached by a gear next to the Coffer wordmark — it replaced the standalone About
`(i)` dialog. Tabbed like per-ledger Settings: **About** (version info, for
everyone) + **Backups** (admin-only tab; the tab self-hides for non-admins, and
the API is RequireAdmin regardless). The Backups panel is modeled on Snapshots:
set/rotate passphrase, *Create*, a list with download/delete, the
auto-backup-daily toggle (the generalized `ScheduleControl`, gated until a
passphrase is set), and the retention policy shown inline. System-wide, so it's
distinct from the per-ledger Settings (snapshots/feeds/quotes/dashboard).

**Restore.** An **operator CLI step**, not an authed UI action — restore runs on
a *fresh* install before any admin exists (pre-auth). `coffer-api restore <file>
--force` (reuses the `coffer-api` subcommand dispatch). Startup migrations are
**skipped** for `restore` (it lands on an empty DB and rebuilds from the dump);
`pg_restore --no-owner` without `--clean` (DR targets an empty DB; `--clean`
would try to drop the install-managed extensions `pgcrypto`/`pg_trgm`, which
`coffer_service` doesn't own — a residual non-zero exit is tolerated only when
*every* error is benign extension-ownership). Bringing the **same
`COFFER_MASTER_KEK_BASE64`** to the new host means `wrapped_lek` columns decrypt
as-is (no re-wrap), and the sealed backup passphrase opens; WebAuthn login keeps
working if the RP id/domain + authenticator are unchanged.

**Google Drive (deferred Phase 2).** OAuth + a refresh token sealed under the
LEK + scheduled push of the retained artifacts. Designed as a seam now; built
later.

## Slices

1. **Admin role** — `users.is_admin` + first-user-admin + `/me` surfaces it.
2. **Backup engine + CLI** — `pg_dump`/`pg_restore` + chunked AEAD/Argon2id;
   `coffer-api backup` / `restore` subcommands; `postgresql-client` in the image.
   Verified end-to-end with a live docker round-trip (backup → restore into a
   fresh DB; grants + the admin column restored; wrong-passphrase rejected).
3. **Admin + retention + scheduler + UI** — shipped as sub-slices:
   - ③a `RequireAdmin` policy + `is_admin` claim + `SealWithMasterKey` helper.
   - ③b `global_scheduled_jobs` (mig 139) + entity + repo (sealed passphrase).
   - ③c retention store + admin endpoints (create/list/download/delete/passphrase/schedule).
   - ③d the daily backup job via the one `SchedulerService` (`IGlobalScheduledJobHandler`).
   - ③e web: admin sidebar section + `/admin/backups` panel (generalized `ScheduleControl`).
4. **Google Drive sync.**

## Consequences

- One encrypted artifact = the whole install; the cleanest DR + migration story.
- A real admin/operator concept enters the model (foundation for future
  system-admin tooling, e.g. user management).
- Identity (WebAuthn credentials) rides along in the dump; restore preserves
  login only when the domain/RP id + authenticator are unchanged — a *new*
  domain forces a passkey re-enroll (data intact).
- Restore stays a CLI/operator action by necessity (pre-auth on a fresh box);
  the UI only creates/manages/downloads.
- TBD (own ADRs when reached): cross-schema-version restore (refuse for now),
  Drive OAuth specifics.

## Amendment — 2026-10-15: a backup is written whole, read back, then listed

Every backup taken while a snapshot existed was unrestorable for two months (the chunk
table's data without its parents) and every scheduled run reported success, because the
only check after `pg_dump` was the file's size. The write path now has three guarantees:

- **Atomic.** The artifact is written to `<name>.cofferbak.partial`, flushed to disk,
  verified, and only then renamed to its listed name. The `.partial` never matches the
  store's `*.cofferbak` listing, so an unfinished write cannot be listed, counted fresh by
  the backup-age monitor, mirrored to Drive, or kept by retention. Startup removes any
  leftover `.partial`.
- **Verified.** After the write the artifact is decrypted in full (every chunk tag is
  authenticated), its table of contents is read with `pg_restore --list`, and it must hold
  `TABLE DATA` for every ordinary table in the live catalog except the declared exclusions
  (`BackupService.SnapshotDataExclusions`, the one list the dump and the check share). A
  failure deletes the artifact and fails the backup through the existing `backup.failed`
  path; retention does not run, so a failed backup never costs a good one. The decrypted
  dump is plaintext on disk for the check: an owner-only directory, removed in a `finally`,
  and swept at the next verification if a kill left it behind. `coffer-api backup --out`
  goes through the same write, verify, rename steps.
- **Retention keeps the newest.** `SelectForDeletion` never selects the newest backup,
  whatever the tiers say, and the daily tier is at least one day (migration 250 raises a
  stored 0 to 1). Previously 0/0/0 deleted each new backup right after writing it while the
  create answered 201.

What verification does not prove: that the data restores. That is the restore drill's job
(FU-059).

## Amendment — 2026-10-16: a backup reports what actually happened to it

Four ways a backup's own report was wrong, closed together (FU-063, FU-065, FU-067):

- **A manual backup no longer runs inside its request.** `POST /api/admin/backups` answers
  **202** with a run id, the backup runs on the host's lifetime (`BackupRunCoordinator`),
  and the browser polls `GET /api/admin/backups/runs/{runId}`. It used to answer 201 at the
  end, so a proxy's request timeout (Cloudflare's is 100 seconds) cancelled the backup with
  it. The run's state is in memory: after a restart the run is unknown (404) and the list is
  where to look.
- **One backup at a time.** Nothing serialised a manual and a scheduled backup. Every backup
  now takes one gate: a scheduled run waits for a manual one; a manual start while another
  runs is refused (422 `backup-in-progress`).
- **An interrupted backup leaves a trace.** A run cut short by a stop or deploy was claimed
  and then left with no outcome; only the 48-hour age alert noticed. The scheduler now
  records it as a failure with the reason "Interrupted" — counted toward the five-failure
  auto-disable like any other failure, by the maintainer's choice — and both the scheduled
  and the manual path publish `backup.failed`, on a token of their own.
- **A good backup is never announced as failed.** The success heartbeat was published inside
  the `try` that announces failures, so a failed event write turned a written, verified,
  listed backup into a `backup.failed` and a counted failure. It is now published after,
  and a write that fails is logged. A retention pass that throws after the write (a file
  vanishing under the listing) is likewise logged and retried by the next backup.
- **A failed off-host copy is said out loud.** A push failure still never fails the backup —
  the local copy is safe — but it now publishes `drive.push-failed` (Warning), and
  `DriveSyncMonitor` raises `drive.stale` (Critical, throttled like `backup.stale`) when no
  backup has reached Drive for 48 hours. It measures from the newest
  `drive_mirrored_backups.mirrored_at`, not `last_sync_at`, which every failed attempt
  refreshes.

## Amendment — 2026-10-16: archive format v3, and a header that is checked before it is used

Two weaknesses in the `.cofferbak` header (FU-072, audit R1 and R2):

- **Nothing authenticated it.** The per-chunk tags covered the sequence and the final flag,
  not the header, so its version could be downgraded and its KEK fingerprint swapped or
  stripped — which is what let an edited archive steer key adoption. **v3** has v2's layout
  and binds the whole header into every chunk's associated data: any change to it fails every
  tag, so the passphrase check every restore runs before committing now also proves the header
  is the one the backup was written with. Writers emit v3; v1 and v2 still read. An earlier
  Coffer cannot read a v3 backup and says so by version, which is the same answer a backup
  from a newer schema already got.
- **Its KDF parameters were used as read.** Argon2 runs to completion and cannot be
  cancelled, so a crafted header asking for terabytes of memory or a billion passes pinned
  the process — on the restore endpoints, one of which a setup-token holder can reach.
  Parameters are now bounded before anything is derived: memory at most four times, and
  passes and lanes a small multiple of, the 64 MiB / 3 / 1 every writer has used. A header
  outside them is refused from the header alone, by the pre-flight read as well.
