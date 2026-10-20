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

## Amendment — 2026-10-16: the mirror deletes only what this install put there

D1's delete rule — remove every Drive file whose id is not a current local backup — had
one guard, skip when the local set is empty, and it never fired: the mirror runs straight
after a backup, so the set always holds that backup. Point the backup directory at a new
volume, or mount an empty one, and the next backup left one local file and deleted every
older Drive copy (FU-064). The off-site history is exactly what such a mistake should not
be able to reach.

A remote backup may now be deleted only if ALL of these hold:

- **this install uploaded it** — it is listed in `drive_mirrored_backups` (migration 251);
- it is **no longer local**, and **not pinned**;
- and either the operator **deleted it here** (the delete endpoint marks it), or
  **retention would drop it** — applied to the local backups *plus* every backup this
  install has mirrored, not to the local folder alone.

The last condition is what an emptied or swapped folder meets: the other Drive copies
still count toward the policy, so only the genuinely aged ones go. Deleting a backup in
the UI still removes its Drive copy at the next sync, as before.

**What this reverses, by the maintainer's choice of 2026-10-16:** D1 swept strays and
legacy `*.ledgrbak` files, and the Consequences below call that "a clean sweep the user
explicitly chose". Those files are now left alone, because "never delete what this install
did not upload" admits no exception for them; the mirror logs how many it left. The
connection that created them is gone, and a restore or a disconnect clears the list, so a
later connection never deletes on an earlier one's word.

**Also (FU-066):** the folder listing reads every page (it read one page of 1000 and
ignored the next-page token, so the delete side could act on a remote set it had not fully
seen), and an upload is accepted only when Drive reports the size and MD5 of the bytes
sent; a mismatch deletes the upload and fails the push.
