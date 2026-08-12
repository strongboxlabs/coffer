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
- The **next boot**, before serving/migrating, applies the staged restore via
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
