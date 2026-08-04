-- =============================================================================
-- 142 — drive_sync: Google Drive backup destination config (ADR-0062 §④a)
-- =============================================================================
--
-- Deployment-wide (non-ledger) singleton config for off-host backup sync to
-- Google Drive. Off by default; nothing happens until an admin connects an
-- account. The OAuth material (client_id + client_secret + refresh_token) is
-- sealed as ONE blob under the master KEK (LedgerKeyService.SealWithMasterKey,
-- the same primitive the backup passphrase uses, ADR-0060) — never plaintext —
-- and is re-wrapped by `rotate-kek` alongside the LEKs + passphrase (ADR-0062
-- D3). Only the encrypted `.cofferbak` is ever uploaded; the passphrase + KEK
-- never leave the host (D1).
--
-- Single-row by construction (id = 1). Service-role only, same posture as
-- global_scheduled_jobs (mig 139): RLS deny-all for coffer_app, the scheduler
-- (BYPASSRLS) + the admin write path (service role) read/write it.
-- =============================================================================

CREATE TABLE drive_sync (
    id                  SMALLINT    NOT NULL DEFAULT 1,
    enabled             BOOLEAN     NOT NULL DEFAULT FALSE,
    -- Sealed {client_id, client_secret, refresh_token} JSON (master KEK).
    -- NULL until an admin completes the device-code connect.
    oauth_ciphertext    BYTEA,
    -- The Coffer-owned Drive folder this destination manages (folder isolation).
    folder_id           TEXT,
    folder_name         TEXT,
    -- The Google account the refresh token belongs to (display only).
    connected_email     TEXT,
    -- Per-destination GFS retention (Drive is the long-term home; independent
    -- of local retention, ADR-0062 D5). Defaults match the local tiers.
    retention_daily     SMALLINT    NOT NULL DEFAULT 7,
    retention_weekly    SMALLINT    NOT NULL DEFAULT 8,
    retention_monthly   SMALLINT    NOT NULL DEFAULT 12,
    last_sync_at        TIMESTAMPTZ,
    last_sync_status    TEXT,
    last_sync_error     TEXT,
    configured_by_user_id UUID      REFERENCES users(id) ON DELETE SET NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_drive_sync PRIMARY KEY (id),
    CONSTRAINT ck_drive_sync_singleton CHECK (id = 1),
    CONSTRAINT ck_drive_sync_retention CHECK (
        retention_daily >= 0 AND retention_weekly >= 0 AND retention_monthly >= 0)
);

COMMENT ON TABLE drive_sync IS
    'ADR-0062: singleton Google Drive backup-destination config. OAuth sealed '
    'under the master KEK. RLS deny-all; service role only.';

-- RLS: enabled + forced, no policy → deny-all for coffer_app. Only BYPASSRLS
-- (service role / scheduler) touches it.
ALTER TABLE drive_sync ENABLE ROW LEVEL SECURITY;
ALTER TABLE drive_sync FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE drive_sync FROM coffer_app;
GRANT  ALL ON TABLE drive_sync TO   coffer_service;
