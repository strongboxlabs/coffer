-- 160 — Drop the Drive-side GFS retention (ADR-0074 mirror model).
--
-- The Google Drive folder now MIRRORS the local backup set: local retention
-- (BackupStore) is the single source of truth for what to keep, and the Drive
-- reconcile just makes the folder equal the local set (upload missing, delete
-- extras). The per-destination GFS tiers on drive_sync are therefore obsolete —
-- drop the CHECK and the three columns.

ALTER TABLE drive_sync DROP CONSTRAINT IF EXISTS ck_drive_sync_retention;
ALTER TABLE drive_sync DROP COLUMN IF EXISTS retention_daily;
ALTER TABLE drive_sync DROP COLUMN IF EXISTS retention_weekly;
ALTER TABLE drive_sync DROP COLUMN IF EXISTS retention_monthly;
