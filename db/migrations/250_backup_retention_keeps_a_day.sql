-- 250 — backup_settings: the daily retention tier is at least one day.
--
-- Migration 161 allowed 0/0/0. At that setting no tier selected the backup that was
-- just written, so the retention pass deleted it straight after writing it, while the
-- create answered 201 and the scheduled job reported success (FU-062). Retention now
-- also never deletes the newest backup (BackupStore.SelectForDeletion); this makes the
-- stored policy say what it does: at least one daily backup is kept.
--
-- A row already at 0 is raised to 1. Nothing else about it changes (updated_at and the
-- configuring user are left as they were), so the migration is a pure function of the
-- existing row.

UPDATE backup_settings SET retention_daily = 1 WHERE retention_daily < 1;

ALTER TABLE backup_settings DROP CONSTRAINT ck_backup_settings_retention;
ALTER TABLE backup_settings ADD CONSTRAINT ck_backup_settings_retention CHECK (
    retention_daily >= 1 AND retention_weekly >= 0 AND retention_monthly >= 0);
