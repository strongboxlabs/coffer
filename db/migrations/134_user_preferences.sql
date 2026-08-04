-- =============================================================================
-- 134 — user_preferences (ADR-0057): general per-(user, ledger) preference store
-- =============================================================================
--
-- One general table, not a table per feature. One row per
-- (user, ledger, namespace); `value` is a namespace-typed JSON document (the
-- provider_runs.details pattern). A new preference area is a new `namespace`
-- (and a new typed record + validator in the API) — never a schema change.
--
-- First consumer (ADR-0057 D4): the `quotes` namespace
-- ({ "enabledProviders": [...] }) — the per-ledger opt-in for external
-- market-data providers (Yahoo), replacing the ADR-0054 Quotes:Yahoo:Enabled
-- config gate. Default-absent = opt-out (no external egress).
--
-- Scope: per (user, ledger). A scheduled run (ADR-0054 B, future) executes as
-- the system user and reads the system user's pref for the ledger (ADR-0055
-- attribution); the system user holds its own rows like any user.
--
-- NOT part of ledger snapshots (ADR-0037): this is per-user UI config, not
-- ledger financial data, so a snapshot restore leaves it untouched (it is
-- neither captured nor wiped).
-- =============================================================================

CREATE TABLE user_preferences (
    user_id    UUID        NOT NULL,
    ledger_id  UUID        NOT NULL,
    namespace  TEXT        NOT NULL,
    value      JSONB       NOT NULL DEFAULT '{}'::jsonb,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_user_preferences PRIMARY KEY (user_id, ledger_id, namespace),
    CONSTRAINT fk_user_preferences_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
    CONSTRAINT fk_user_preferences_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers (id) ON DELETE CASCADE,
    CONSTRAINT ck_user_preferences_namespace CHECK (namespace <> '')
);

COMMENT ON TABLE user_preferences IS
    'ADR-0057: general per-(user, ledger) UI preference store. One row per '
    '(user, ledger, namespace); value is a namespace-typed JSON document. NOT '
    'part of ledger snapshots (ADR-0037) — per-user UI config, not ledger '
    'financial data, so a restore leaves it untouched.';

-- RLS — own-user AND per-ledger visibility (flattened policy, migs 071/072/127).
-- A user reads/writes only their OWN preference rows, and only for ledgers they
-- can see.
ALTER TABLE user_preferences ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_preferences FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS user_preferences_per_user ON user_preferences;
CREATE POLICY user_preferences_per_user ON user_preferences
    FOR ALL
    TO coffer_app
    USING (user_id = current_app_user_id()
           AND ledger_id IN (
               SELECT ulg.ledger_id FROM user_ledger_grants ulg
               WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (user_id = current_app_user_id()
           AND ledger_id IN (
               SELECT ulg.ledger_id FROM user_ledger_grants ulg
               WHERE ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE ON user_preferences TO coffer_app;
GRANT ALL ON user_preferences TO coffer_service;
