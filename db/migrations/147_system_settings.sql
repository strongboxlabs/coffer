-- =============================================================================
-- 147 — system_settings (ADR-0063 §D8): deployment-wide key/value settings
-- =============================================================================
--
-- A generic, deployment-global settings store — the home for install-wide
-- (non-ledger, non-user) configuration that an admin should be able to change
-- from the UI rather than by editing env/compose. The first inhabitant is
-- `mcp.enabled` (the MCP server runtime toggle, D8); future System-settings
-- tabs slot their flags in here too.
--
-- Deployment-global, service-role only (same posture as global_scheduled_jobs /
-- drive_sync / backup_pins): RLS deny-all to coffer_app, all access through
-- coffer_service via ServiceDbContextFactory. The admin HTTP surface gates writes
-- with the RequireAdmin policy — admin is a deployment-global capability, not
-- per-ledger, so RLS is not the boundary here.
--
-- `value` is JSONB so the store stays general (a boolean today, a number/object
-- tomorrow) without a schema change per setting.
--
-- The MCP enablement gate is read at STARTUP (Program.cs), so flipping
-- `mcp.enabled` here takes effect on the next API restart — a runtime gate would
-- leave the OAuth AS / /mcp endpoints present-but-404, contradicting the
-- "surface absent when off" hardening (ADR-0063 §D7).
-- =============================================================================

CREATE TABLE system_settings (
    key         TEXT        NOT NULL,
    value       JSONB       NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Who last changed it; SET NULL on user delete so the audit column survives.
    updated_by  UUID        REFERENCES users(id) ON DELETE SET NULL,

    CONSTRAINT pk_system_settings PRIMARY KEY (key),
    CONSTRAINT ck_system_settings_key CHECK (key <> '')
);

COMMENT ON TABLE system_settings IS
    'ADR-0063 D8: deployment-global key/value settings (JSONB value), admin-'
    'writable from the System-settings UI. First key: mcp.enabled (MCP runtime '
    'toggle, read at startup). RLS deny-all; service role only — the RequireAdmin '
    'policy is the boundary.';

ALTER TABLE system_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE system_settings FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE system_settings FROM coffer_app;
GRANT  ALL ON TABLE system_settings TO   coffer_service;

-- Seed the MCP toggle off (ADR-0063 §D7/D8 off-by-default). ON CONFLICT keeps a
-- re-run idempotent and never clobbers an admin's later choice.
INSERT INTO system_settings (key, value)
VALUES ('mcp.enabled', 'false'::jsonb)
ON CONFLICT (key) DO NOTHING;
