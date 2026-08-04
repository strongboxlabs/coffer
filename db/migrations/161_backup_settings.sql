-- 161 — backup_settings: admin-editable backup retention (ADR-0074 / ADR-0060).
--
-- Retention (the GFS tiers) was startup-only config (ApiOptions), so an operator
-- couldn't change it without redeploying. Make it a persisted, admin-editable
-- policy. Singleton (id = 1). It is the SINGLE source of truth: it governs both
-- local backups and the Google Drive mirror (which now just reflects the local
-- set). Service-role only (RLS deny-all), same posture as drive_sync.

CREATE TABLE backup_settings (
    id                    SMALLINT    NOT NULL DEFAULT 1,
    retention_daily       SMALLINT    NOT NULL DEFAULT 7,
    retention_weekly      SMALLINT    NOT NULL DEFAULT 8,
    retention_monthly     SMALLINT    NOT NULL DEFAULT 12,
    configured_by_user_id UUID        REFERENCES users(id) ON DELETE SET NULL,
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_backup_settings PRIMARY KEY (id),
    CONSTRAINT ck_backup_settings_singleton CHECK (id = 1),
    CONSTRAINT ck_backup_settings_retention CHECK (
        retention_daily >= 0 AND retention_weekly >= 0 AND retention_monthly >= 0)
);

COMMENT ON TABLE backup_settings IS
    'ADR-0074: singleton, admin-editable backup retention (GFS tiers). Single '
    'source of truth for local backups + the Google Drive mirror. RLS deny-all; '
    'service role only.';

-- Seed the singleton with the historical defaults (match the old ApiOptions).
INSERT INTO backup_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- RLS: enabled + forced, no policy → deny-all for coffer_app. Only BYPASSRLS
-- (service role / scheduler) touches it.
ALTER TABLE backup_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE backup_settings FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE backup_settings FROM coffer_app;
GRANT  ALL ON TABLE backup_settings TO   coffer_service;
