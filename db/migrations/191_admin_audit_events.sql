-- =============================================================================
-- 191 — admin_audit_events (ADR-0092 D2): durable record of deployment-level
--       administrative actions on key material
-- =============================================================================
--
-- The master KEK can now be viewed and rotated from the UI. Both are actions an
-- operator should be able to account for after the fact — "who saw the key, and
-- when" — and until now no admin action was recorded anywhere but the application
-- log, which rotates away and isn't queryable.
--
-- Scope is deliberately administrative-and-global, not "everything". The two
-- existing audit surfaces don't fit and shouldn't be stretched: `ledger_operations`
-- (ADR-0055) is per-ledger and scoped by RLS, and `mcp_tool_invocations` (ADR-0081)
-- is the MCP write audit. This table is for actions that belong to the DEPLOYMENT.
--
-- Deployment-global, service-role only (same posture as system_settings /
-- global_scheduled_jobs / drive_sync): RLS deny-all to coffer_app, all access via
-- coffer_service. The RequireAdmin policy is the boundary, since admin is a
-- deployment-global capability rather than a per-ledger one.
--
-- `action` is intentionally NOT constrained by a CHECK. The webauthn flow CHECK has
-- now needed widening three times (migrations 140, 176, 190) purely to admit a new
-- string, and an audit table is exactly the kind of thing that grows new event
-- types; the vocabulary lives in AdminAuditActions in C# instead.
--
-- NOT pruned by AuditRetentionService. `Api:AuditRetentionDays` trims the MCP and
-- ledger-operation logs because they are high-volume operational records. Key
-- access is neither: these rows are rare (a handful per install, ever) and their
-- value is precisely that they are old. Retaining them indefinitely costs nothing
-- and losing them defeats the point.
-- =============================================================================

CREATE TABLE admin_audit_events (
    id          UUID        NOT NULL DEFAULT gen_random_uuid(),
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Stable event name; see AdminAuditActions. No CHECK, on purpose (above).
    action      TEXT        NOT NULL,
    -- Who did it. SET NULL on user delete: the event must outlive the account,
    -- otherwise removing a user erases the record of what they did.
    actor_user_id UUID      REFERENCES users(id) ON DELETE SET NULL,
    -- Free-text context ("rotated to v2; 3 ledger key(s)"). Never key material.
    detail      TEXT,

    CONSTRAINT pk_admin_audit_events PRIMARY KEY (id),
    CONSTRAINT ck_admin_audit_events_action CHECK (action <> '')
);

-- The only query shape this table has: newest-first, optionally filtered by action.
CREATE INDEX ix_admin_audit_events_occurred_at
    ON admin_audit_events (occurred_at DESC);

COMMENT ON TABLE admin_audit_events IS
    'ADR-0092 D2: durable log of deployment-level admin actions on key material '
    '(master-KEK view/rotate, restore key adoption). RLS deny-all, service role '
    'only — RequireAdmin is the boundary. Deliberately NOT pruned by '
    'AuditRetentionService: these rows are rare and their value is their age.';

COMMENT ON COLUMN admin_audit_events.detail IS
    'Human-readable context. MUST NOT contain key material, passphrases, or '
    'ciphertext — this table is readable by any admin.';

ALTER TABLE admin_audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin_audit_events FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE admin_audit_events FROM coffer_app;
GRANT  ALL ON TABLE admin_audit_events TO   coffer_service;
