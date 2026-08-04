# 0060 — Whole-DB encrypted backup, admin role, Drive-later

Status: Accepted

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
