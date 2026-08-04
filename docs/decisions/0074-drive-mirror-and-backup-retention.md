# 0074 — Google Drive mirrors the local backup set; one editable retention policy

Status: Accepted
Date: 2026-07-10
Relates: [ADR-0060](0060-whole-db-backup-and-admin-role.md), [ADR-0062](0062-google-drive-backup-sync.md)

## Context

ADR-0062 gave the Google Drive backup destination its **own** retention (GFS tiers
on `drive_sync`), pruned independently of local backups. Combined with a separate
count-based local retention (`ApiOptions`, startup-only), the two sets diverged
**by design** — the Drive folder was an independent archive, not a copy of what
the app shows. In practice that produced a folder the user couldn't reconcile
with the app's backup list:

- **orphans** — Drive's GFS kept tiers local had already pruned, and a local
  delete never propagated to Drive;
- **legacy artifacts** — pre-rename `*.ledgrbak` files the sweep didn't recognize
  (it only stripped `.cofferbak`), so they accumulated forever;
- **uncorrelatable names** — no way to line a Drive file up with a UI row.

The user's requirement was simple: **the folder should equal the app's backup
list.** Separately, retention was startup-only config — no way to change it
without a redeploy, and no UI.

## Decisions

### D1 — The Drive folder mirrors the local backup set

Drive is no longer an independent archive. On each sync (auto-push-on-backup or
manual "Sync now") `GoogleDriveBackupDestination` **mirrors**: upload every local
backup missing from Drive, then delete every Drive file whose bare id isn't a
current local backup — matched across ANY extension, so legacy `*.ledgrbak`
artifacts and strays are swept too. There is no separate Drive retention. A pin
needs no Drive-specific handling: a pinned backup is one local retention keeps, so
it stays in the local set and therefore on Drive.

**Safety:** the delete side is skipped when there are zero local backups, so a
wiped / unmounted backups directory can never empty the cloud copies.

Migration 160 drops the `drive_sync` retention columns.

### D2 — One admin-editable retention policy, persisted

Retention is a single GFS policy (daily days / weekly weeks / monthly months)
persisted in a `backup_settings` singleton (migration 161), editable at
`GET/PUT /api/admin/backups/retention` and in the Backups settings UI. It is the
single source of truth: `BackupManager` resolves it and hands it to `BackupStore`
at prune time (no longer a startup `ApiOptions` value) and — since Drive mirrors
local — it transitively governs the Drive folder too. Replaces the former
`ApiOptions.Retention*` config. The 3-tier GFS model is kept (not simplified to
keep-last-N), per the user's choice.

### D3 — Restore resets the Drive connection

The whole-DB backup includes `drive_sync` (install_id, folder, sealed OAuth). A
restore into a **different** install (dev clone, DR drill, migration) would
otherwise make the restored install impersonate the source's Drive folder — and,
once reconnected, the mirror would **delete** the source's backups from it. So
`BackupService.RestoreAsync` clears `drive_sync` (connection + `install_id`) after
pg_restore; the restored install starts Drive-disconnected and mints a fresh
`install_id` on reconnect → its own folder. **Always** reset, not KEK-conditional:
safety over saving one reconnect on a same-install rollback, and the sealed OAuth
is KEK-unusable across installs anyway.

(Note: the authed-admin restore already validates the backup's KEK fingerprint vs
the install's KEK and warns on mismatch — ADR-0071 D4. That check is *not* on the
CLI / bootstrap restore paths, which is how a dev clone can silently land on a
mismatched KEK.)

## Consequences

- The Drive folder always equals the app's backup list (to within the latest
  sync). The first mirror after upgrading sweeps pre-existing extras — legacy
  formats, orphans, and any deeper history Drive held beyond local — a clean
  sweep the user explicitly chose.
- Changing the retention policy takes effect on the next backup, for both local
  and Drive.
- The `drive_sync` "phantom" / uncorrelatable-name issues (the parked ADR-0062
  follow-up) are resolved structurally: a mirror deletes anything not in the
  local set, so a stray Drive file can't persist.
