-- 174 — Role-aware RLS: viewers read, only owner/editor write (ADR-0083 D2)
--
-- Until now every ledger-scoped table carried ONE policy:
--   CREATE POLICY <t>_per_user ON <t> FOR ALL TO coffer_app
--     USING (ledger_id IN (SELECT ledger_id FROM user_ledger_grants
--                          WHERE user_id = current_app_user_id()));
-- i.e. presence-of-grant = full read+write. The owner/editor/viewer role
-- (ADR-0020) was never enforced at the DB, so a `viewer` grant could write.
--
-- This is the DB half of ADR-0083's defense-in-depth (the API `LedgerAuthorizer`
-- is the primary check). Per table we replace that single policy with TWO:
--   <t>_read  FOR SELECT      USING (any grant)                     -- viewers see data
--   <t>_write FOR ALL         USING/CHECK (role IN owner|editor)    -- only writers mutate
-- Postgres OR-combines permissive policies, so SELECT passes via _read while
-- INSERT/UPDATE/DELETE satisfy only _write. The write predicate simply adds
-- `AND role IN ('owner','editor')` to the existing inlined subquery (planner
-- parity — no per-row function call).
--
-- SCOPE: migrations 071/072 flattened the child tables to filter by ledger_id
-- DIRECTLY, so there is no FK-composition backstop — every ledger-DATA table
-- must be covered (not just anchors). The set below is the live catalog's
-- ledger_id-bearing FOR-ALL coffer_app tables, minus the deliberate exclusions.
--
-- DELIBERATELY EXCLUDED (kept at their current any-grant policy, NOT role-gated):
--   * user_preferences, user_account_groups, user_account_group_members —
--     user-OWNED view state (a viewer arranging their own dashboard/groupings
--     for a ledger they can see is legitimate; not ledger data).
--   * mcp_tool_invocations — security AUDIT of MCP write-tool calls. Must be able
--     to record a blocked/rejected attempt (which runs in the attempting user's
--     context), so it cannot be gated on write-role.
--   * provider_runs / provider_run_errors / provider_run_promotions — sync/quote
--     RUN audit. The ingest/quote worker writes these via the BYPASSRLS service
--     role (ADR-0020 Rule 4); who may TRIGGER a run is gated at the API. Gating
--     the audit rows would add nothing and risks the pipeline's own bookkeeping.
--   * scheduled_jobs — system scheduler state (service-written; config API-gated).
--   * ledgers / user_ledger_grants — already SELECT-only for coffer_app; their
--     writes go through the service role, gated by the API authority checks.
--
-- All exclusions are ledger membership/audit/system rows, not the books. A
-- viewer still cannot reach their write paths (service-role or API-gated).

DO $$
DECLARE
    t text;
    -- Ledger DATA a viewer must NOT mutate. Grounded in the live pg_policies /
    -- information_schema catalog (ledger_id column + FOR ALL coffer_app policy).
    ledger_data_tables text[] := ARRAY[
        'accounts',
        'account_external_ids',
        'feed_connections',
        'feed_connection_accounts',
        'holdings',
        'loan_terms',
        'lots',
        'provider_security_mappings',
        'realized_gains',
        'recurring_occurrence_exceptions',
        'recurring_transactions',
        'securities',
        'security_prices',
        'security_splits',
        'tags',
        'txn_header_account_balances',
        'txn_header_overrides',
        'txn_header_tags',
        'txn_headers',
        'txn_leg_overrides',
        'txn_leg_recon',
        'txn_legs'
    ];
BEGIN
    FOREACH t IN ARRAY ledger_data_tables LOOP
        -- Fail loud if the model drifts: every listed table must carry ledger_id.
        IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = t AND column_name = 'ledger_id'
        ) THEN
            RAISE EXCEPTION 'role-aware RLS (mig 174): table % is missing a ledger_id column', t;
        END IF;

        EXECUTE format('DROP POLICY IF EXISTS %I ON %I', t || '_per_user', t);

        EXECUTE format(
            'CREATE POLICY %I ON %I FOR SELECT TO coffer_app '
            'USING (ledger_id IN (SELECT ledger_id FROM user_ledger_grants '
            '                     WHERE user_id = current_app_user_id()))',
            t || '_read', t);

        EXECUTE format(
            'CREATE POLICY %I ON %I FOR ALL TO coffer_app '
            'USING (ledger_id IN (SELECT ledger_id FROM user_ledger_grants '
            '                     WHERE user_id = current_app_user_id() '
            '                       AND role IN (''owner'', ''editor''))) '
            'WITH CHECK (ledger_id IN (SELECT ledger_id FROM user_ledger_grants '
            '                          WHERE user_id = current_app_user_id() '
            '                            AND role IN (''owner'', ''editor'')))',
            t || '_write', t);
    END LOOP;
END $$;

-- security_components has NO ledger_id — it composes through the securities FK
-- (ADR-0067 look-through sleeves). Gate it via its parent security's ledger role.
DROP POLICY IF EXISTS security_components_per_user ON security_components;

-- Read: any grant. The subquery is itself RLS-filtered by securities' _read
-- policy, so it already scopes to the caller's ledgers.
CREATE POLICY security_components_read ON security_components FOR SELECT TO coffer_app
    USING (security_id IN (SELECT id FROM securities));

-- Write: only owner/editor of the security's ledger.
CREATE POLICY security_components_write ON security_components FOR ALL TO coffer_app
    USING (security_id IN (
        SELECT s.id FROM securities s
        WHERE s.ledger_id IN (SELECT ledger_id FROM user_ledger_grants
                              WHERE user_id = current_app_user_id()
                                AND role IN ('owner', 'editor'))))
    WITH CHECK (security_id IN (
        SELECT s.id FROM securities s
        WHERE s.ledger_id IN (SELECT ledger_id FROM user_ledger_grants
                              WHERE user_id = current_app_user_id()
                                AND role IN ('owner', 'editor'))));
