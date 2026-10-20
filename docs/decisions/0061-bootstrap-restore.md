# 0061 — Bootstrap restore: restore-from-backup in the setup UI

Status: Accepted — amended by [ADR-0094](0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md), which removed the
`coffer-api restore` CLI this ADR kept "for headless DR and very large backups".
Date: 2026-06-24
Amends: [ADR-0060](0060-whole-db-backup-and-admin-role.md)

## Context

ADR-0060 made whole-DB restore an **operator CLI step** (`coffer-api restore`),
because restore runs on a fresh install before auth/admin exists and replaces the
entire database. The disaster-recovery drill confirmed that works but is fiddly
(manual file copy, init-script perms, start-order). A fresh install already shows
a setup ceremony (create the first user + ledger), and that **pre-auth, no-admin
window is exactly the safe time to restore**. So the setup screen should offer
**Create a new install** OR **Restore from a backup** — making restore a
first-class UI flow.

## Decision

The setup page (`/setup/{token}`) opens with a choice. The restore branch uploads
a `.cofferbak` + passphrase to **`POST /api/auth/setup/{token}/restore`**
(anonymous, **bootstrap-token-gated** like the rest of setup — the token only
exists before the first user, so this is first-run only).

**Why not restore in-process:** the UI restores over a *live, already-migrated*
DB (the server had to migrate to serve the page + hold the token), and the API
runs as the non-superuser `coffer_service` with install-managed extensions
(`pg_trgm`/`pgcrypto`) it can't drop or recreate. So the flow is **stage →
restart → apply at boot**:

- The endpoint **stages** the artifact + passphrase under `data/restore-staging/`
  — after verifying the passphrase actually opens the archive, so a wrong one
  fails *here*, not in a post-restart boot loop — writes a marker, and requests a
  **restart** (via the injectable `IApplicationRestarter`).
- *(Superseded 2026-10-09 — see the amendment at the end: the restore is now
  built as a new database and swapped in.)* The **next boot**, before
  serving/migrating, applies the staged restore via
  `BackupService.RestoreAsync(clean: true)`: `pg_restore --clean --if-exists`
  drops the app objects and rebuilds from the dump; the extension DROP/COMMENT
  failures are the same benign "must be owner of extension" class ADR-0060
  already tolerates. It then **shreds** the staging and serves the restored DB.
- The SPA shows a "restoring…" screen and polls until the server is back, then
  lands on `/login` — sign in with the restored credentials (key / recovery
  code).

`coffer-api restore` **stays** for headless DR and very large backups (the UI
upload's practical ceiling is the ~128 MB multipart limit; Kestrel's per-request
cap is lifted for the endpoint).

## Consequences

- Restore is no longer CLI-only (amends ADR-0060) — it's a first-class bootstrap
  flow *and* the CLI.
- The passphrase sits briefly in `data/restore-staging/` between upload and the
  applying boot, then is shredded — acceptable on the Layer-1-encrypted volume
  (operations.md), where the encrypted artifact already lives.
- A failed apply-at-boot clears the request (no boot loop) and logs loudly; since
  the passphrase is pre-verified at upload, the common failure (wrong passphrase)
  never reaches the boot. A pg_restore failure mid-apply (rare; disk, etc.) can
  leave a partial DB — the operator re-runs setup/restore.
- Relies on the container restart policy (compose `restart: unless-stopped`);
  outside Docker the operator restarts the process.

## Amendment — 2026-10-09: restore by database swap

**What failed.** A production restore of the previous night's backup reported
"Restore complete" and changed nothing. Two in-place mechanisms had replaced the
original `--clean --if-exists` (fragile over a populated or cross-version
schema): a wipe of everything the service role owns, then `pg_restore` into the
empty schema. The wipe enumerated object kinds by hand. Migration 200 added an
aggregate (`DROP FUNCTION` refuses one) and 202 two composite types the list did
not know, so **from v0.62.0 every restore over an existing install failed** at the
wipe. The wipe is one `DO` block, so the database was left untouched — by luck: the
`pg_restore` after it was not transactional, and a failure there would have left
a wiped, half-loaded database. The boot logged `Bootstrap restore FAILED`, booted
the old database, and the browser — which only polled `/healthz` — said "Restore
complete". Separately, every backup taken while a snapshot existed was itself
unrestorable: the dump kept `ledger_snapshot_parts` (the payload since mig 193)
while excluding its parent `ledger_snapshots`, so the FK between them failed.

**Decision.**

1. **Restore builds a NEW database and swaps it in.** At boot:
   decrypt and authenticate the whole archive → `CREATE DATABASE
   {name}_restore_{utc}` (template0, the live database's encoding and collation)
   → `pg_restore --exit-on-error` into it (the extensions come from the dump:
   `pgcrypto` and `pg_trgm` are trusted, so the owner creates them) → run this
   build's migrations **on it** → reset the install's Drive identity → swap with
   two `ALTER DATABASE … RENAME` in **one transaction**. On any failure the new
   database is dropped and the live one was never touched. No object-kind list
   exists any more, so the class of bug that broke production cannot recur.
2. **The replaced database is kept** as `{name}_pre_restore_{utc}` — the way back —
   until the next restore prunes it (one kept at a time; each is a full copy).
3. **Privileges.** The swap needs `coffer_service` to own the live database and hold
   `CREATEDB`. Weighed and accepted: the role already owns every table and can drop
   them all; owning the database adds dropping or renaming it as a whole, and
   `CREATEDB` adds creating databases (a disk consideration, not a data one). It
   gains no superuser power. Fresh installs grant it in `db/init`; existing installs
   get it from `scripts/provision-restore-privileges.sh`, which `install.sh` runs on
   upgrade. `restore_readiness()` (mig 246) reports it, and both restore endpoints
   refuse **before staging** with that fix named — not after a restart, in a log.
4. **The browser reads the outcome.** Staging mints an attempt id; the boot records
   `applied` (with the kept database's name) or `failed` (with the reason) under it;
   `GET /api/restore-outcome/{attemptId}` (anonymous — the session may belong to the
   replaced database — and answering only the latest attempt's id) reads it back. No
   recorded outcome reads as **unconfirmed**, never as success.
5. **Backups exclude `ledger_snapshot_parts` data** as well as `ledger_snapshots`, and
   the restore skips both tables' data, so archives taken before the fix restore too.

**Hardened after an adversarial review** of the first cut, before release:
- **The swap's result is read from the catalog, not psql's exit code.** The incoming
  database is stamped `coffer-restore:<attempt>:<replaced-name>` (its COMMENT) before
  the swap; if the swap reports an error, the live database's stamp decides applied or
  failed, and if even that cannot be read the outcome is `unknown` — never a guess.
- **A boot that died after the swap is recognised**, by the same stamp, instead of
  running the restore again — which would have renamed the restored database away and
  pruned the real pre-restore copy. Pruning older copies happens only after the outcome
  is recorded and the staging cleared.
- **Only the server's own boot applies a staged restore**; a CLI subcommand
  (`coffer-api backup`, `bootstrap-token`) runs the same program and used to race it.
- **A failed restore that had adopted the source install's key puts the original key
  back** and restarts onto it, so "nothing was changed" is true of the key file too.
- **A backup from a newer build is refused** (its migration journal names scripts this
  build does not ship), and the new database is `ANALYZE`d before it goes live.
- **The decrypted archive is owner-only** and swept if a crash left it; the error shown
  to the browser drops lines that can quote row data.
- **Readiness is checked in full at staging** — grants, name length, maintenance
  database, locale provider — and the refusal names the SQL as well as the script.
- **No `DROP DATABASE … WITH (FORCE)`.** The drill once left an abandoned database
  behind; traced to a race with autovacuum, not tolerated as flakiness. FORCE makes a
  non-superuser terminate every other session first, an autovacuum worker counts as a
  superuser's, and a freshly loaded database draws one within a minute — so the drop
  failed ("permission denied to terminate process") exactly when a worker was in it:
  6 of 6 in a probe with a worker present, against 6 of 6 successes for a plain DROP,
  which has Postgres cancel the worker itself. A unit test pins the statement and the
  drill re-proves the Postgres behaviour with a worker provably in the database.

**Proven by** the maintainer's restore drill, which runs the real images: an
install on the previous release with a real dataset (snapshots included), a backup
by that release's own CLI, an upgrade to the new build, provisioning, then the
restore through the boot path — asserting every table equals the backup and the
kept database equals the pre-restore state exactly — and a second restore refused
while another session holds the database. "Every table" means every one a restore
does not leave out by design. The drill gives the app nothing to write by itself
(each monitor's check already on record, a fresh backup in the store, jobs and
retention off) instead of excusing the tables it writes: an excused table is one
whose restore nobody checked, and a calibration that diffed two consecutive starts
once missed a monitor that writes only when its own history says it has not yet.
Run against the previous release as "new", it fails exactly as production did. The old `scripts/backup-restore-roundtrip.sh`
tested a hand-copied duplicate of the wipe on a synthetic schema and is removed.


## Amendment — 2026-10-16: the restore is proven on every push to main

The 2026-10-09 amendment above came from a production restore that reported success and
did nothing, which no test could see because none had ever run the real restore against
the real schema. the restore drill script does, and it ran by hand against a
dataset someone supplied. It is now continuous:

- **Dataset.** Generated, never copied from a ledger: `make-drill-dataset.sh` imports the
  committed synthetic fixtures (`data/samples/`) through the real importer into a database
  at one pinned schema version, then `drill-dataset/seed.sql` adds what an import does not
  produce: snapshots with chunks, and a merge whose winner is stored after its loser
  (`txn_headers.is_merged_into` is a self-reference). The result is a committed
  `pg_dump`. The previous release migrates it forward at boot, so one dataset serves every
  drill; it is regenerated only when the shapes it holds need to change, or when the oldest
  release a drill might use predates its schema (the drill refuses, by name, if so).
- **Previous release.** Built from its source (`git archive` of its tag), not pulled: the
  release registry is private and the CI VM holds no credentials, by design. That is also
  why `preflight-vm.ps1` ships the tree in beside the snapshot, which has no git history.
- **Gate.** `restore-drill.yml` runs it on every push to `main`; `release.sh` refuses a
  release whose commit has no green run (or an ancestor's, with only docs changed since).
  *Amended 2026-10-20:* it runs once per change, on the pull request, and `release.sh`
  accepts a green run on a tree matching the released one apart from docs and the version
  stamp, which covers the squash merge of that PR.
  `--drill-skipped '<why>'` is the recorded escape for when GitHub or the VM cannot be asked.
  The drill is not part of an unqualified preflight run (it is minutes of docker work
  beside nine test shards): `--only drill` or `--with-drill`.
- **Proven able to fail.** Against a build whose restore never swaps the database in, the
  restore reports "applied" and the drill fails four checks: the replaced database is not
  the pre-restore state, the restored database differs from the backup, the change made
  after the backup survives, and the snapshots from before it come back.

What it still does not cover: tables the dataset leaves empty (several feature tables;
`make-drill-dataset.sh` prints them). The sessions, tokens, grants and invites a restore
revokes are seeded before the backup since the 2026-10-17 amendment below.

## Amendment — 2026-10-16: undoing a restore stays a documented procedure, proven by the drill

FU-068 asked for "Undo the last restore" in the admin panel, staged and applied at boot.
Not built, by the maintainer's decision: the replaced database is already kept, going back
is two renames in one transaction (operations.md, "Undoing a restore"), and a second
destructive boot path is one more place a swap can fail, for an operation that is rare and
done by the one operator who has the server's shell anyway. What FU-068 needed was for that
procedure to be known to work, so the restore drill now reads its SQL out of operations.md
and runs it after the restore it tests: the live database must then be the replaced one,
exactly, and the API must serve.

## Amendment — 2026-10-16: what a restore takes in, leaves behind, and still trusts

From the 2026-10-12 audit (FU-072, FU-073, FU-075, FU-079):

- **A free-space floor on uploads.** The parts protocol had no total cap, so an upload could
  fill the data volume — the one holding the master key and the stored backups. Every part,
  every single-request upload and every copied stored backup is refused, before it is
  written, if it would leave less than `Api:Backup:RestoreFreeSpaceReserveMb` (1024) free.
- **No stale key adopted.** Both endpoints clear a staged source key when a new restore
  begins, and a boot will not adopt one older than the archive staged beside it.
- **No row data kept.** The recorded outcome drops PostgreSQL's DETAIL lines ("Failing row
  contains (…)") in both tools' spellings, as it already dropped CONTEXT and Key (…).
- **No plaintext left in temp.** A server boot removes the restore and verification work
  directories (the decrypted dump of the whole database) and unfinished multipart buffers a
  killed run left; before, they waited for the next restore. A server boot only: a CLI command
  exec'd into a running container shares its temp. Off in the test host, whose many servers
  share a machine's temp with other test processes.
- **Secret files written like `master.key`.** The staged passphrase and source key and the
  setup link in `bootstrap.url` are written owner-only from the first byte and renamed into
  place whole (`SecretFile`); they were 0644 and written in place.

**Accepted, by the maintainer's decision of 2026-10-16:** `pg_restore` runs the archive's SQL
as `coffer_service`, which owns the live database and may create databases, so a hostile
archive could change role-level settings that outlive the restore or drop the kept pre-restore
database. Restoring as a throwaway role that owns only the new database would close it, at
the cost of granting `coffer_service` CREATEROLE on every install. Not done: only an admin
(with a passkey tap) or a setup-token holder can submit an archive, the archive must also pass
the passphrase check, and v3 now makes an edited legitimate backup fail that check.

## Amendment — 2026-10-17: a restore signs everyone out

A restore brought every session, MCP token and OAuth grant back exactly as it was at backup
time, revoked ones included, and every pending invite. Incident response backfired: revoke
the attacker, restore a backup from before the incident, and their access came back. The
restore copy said it "signs everyone out", which was false; the restore card even asked
`/api/auth/me` whether the admin's own session had survived (FU-076).

The database a restore builds now has, after its migrations and before the marker that says
it is ready, every cookie session, manual MCP token, OAuth authorization and OAuth token
revoked — the same `revoked_at` or status the per-user revocation sets, so the rows read as
revoked rather than vanishing — and every pending invite removed, which is what revoking an
invite is. Users with their admin and disabled flags, passkeys and recovery codes come back
as backed up: clearing passkeys would lock out an install that moved domains. OAuth client
registrations stay, so a connector signs in again rather than registering anew. Not chosen:
keeping the tokens with a warning, and carrying forward revocations made after the backup.

The reset, and the Drive reset beside it, run through EF in one transaction on the incoming
database (`RestoreReset`); the Drive half used to be a psql script, which the raw-SQL audit
could not see. Both restore screens say what was reset. The restore drill seeds a session, an
MCP token, an OAuth grant with a refresh token and two invites before its backup, and checks
that the kept database still has them live and the restored one has them revoked, the pending
invite gone, and the accepted invite and the OAuth client kept.
