-- =============================================================================
-- 144 — backup_pins: "never delete" pins for backup artifacts (ADR-0062 §④b+c)
-- =============================================================================
--
-- An admin can pin a backup so retention never prunes it — locally OR on Drive.
-- The key is the artifact id (the .cofferbak filename stem, e.g.
-- coffer-20260625T012107576Z-4c2b7336), which is identical for the local file and
-- its Drive copy, so one pin covers both. Deployment-global, service-role only
-- (backups are a deployment capability, not per-ledger) — same posture as
-- global_scheduled_jobs / drive_sync.
-- =============================================================================

CREATE TABLE backup_pins (
    artifact_id        TEXT        NOT NULL,
    pinned_by_user_id  UUID        REFERENCES users(id) ON DELETE SET NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_backup_pins PRIMARY KEY (artifact_id)
);

COMMENT ON TABLE backup_pins IS
    'ADR-0062: admin "never delete" pins keyed by backup artifact id; excludes '
    'the artifact from local + Drive retention. RLS deny-all; service role only.';

ALTER TABLE backup_pins ENABLE ROW LEVEL SECURITY;
ALTER TABLE backup_pins FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE backup_pins FROM coffer_app;
GRANT  ALL ON TABLE backup_pins TO   coffer_service;
