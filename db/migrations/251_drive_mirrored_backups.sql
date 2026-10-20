-- 251 — drive_mirrored_backups: which backups THIS install has put on Google Drive.
--
-- The Drive mirror (ADR-0074) deleted every remote file that was not in the local
-- set, with one guard: not when the local set was empty. It always runs straight after
-- a backup, so the set is never empty — point the backup directory at a new volume, or
-- mount an empty one, and the next backup left one local file and deleted every older
-- Drive copy (FU-064).
--
-- The mirror may now delete a remote backup only if THIS install recorded putting it
-- there, so a stray, a legacy file or another install's upload is never touched, and
-- even then only when it is no longer local AND either the operator deleted it on
-- purpose (deleted_at) or retention, applied to everything this install has mirrored
-- as well as what is local, would drop it. An empty or swapped folder therefore removes
-- only what has genuinely aged out.
--
-- Drive state, like drive_sync: a restore clears it (the restored install gets a new
-- Drive identity), and the restore drill excludes it. Service-role only, RLS deny-all.

CREATE TABLE drive_mirrored_backups (
    backup_id   TEXT        NOT NULL,
    mirrored_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Set when the operator deletes the backup here; the mirror then removes the Drive
    -- copy without waiting for retention. NULL = still wanted.
    deleted_at  TIMESTAMPTZ,

    CONSTRAINT pk_drive_mirrored_backups PRIMARY KEY (backup_id)
);

COMMENT ON TABLE drive_mirrored_backups IS
    'FU-064: ids of backups this install uploaded to Google Drive. The mirror deletes '
    'a remote backup only if it is listed here. Cleared on disconnect and by a restore. '
    'RLS deny-all; service role only.';

ALTER TABLE drive_mirrored_backups ENABLE ROW LEVEL SECURITY;
ALTER TABLE drive_mirrored_backups FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE drive_mirrored_backups FROM coffer_app;
GRANT  ALL ON TABLE drive_mirrored_backups TO   coffer_service;
