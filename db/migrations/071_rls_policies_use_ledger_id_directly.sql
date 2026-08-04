-- =============================================================================
-- 071 — flatten RLS policies on child tables to use ledger_id directly
-- =============================================================================
--
-- THE PERF PROBLEM
--
-- The recompute trigger chain (068 + 069/070) was measured at ~3.5 s
-- per investment save. Diagnosis: not the function bodies — it's RLS
-- cascade. Functions called under `coffer_app` did 1.27 M page reads
-- vs 7 K under superuser (180×). Source: child-table RLS policies use
-- the parent-recursion pattern (`x IN (SELECT id FROM parent)`) and
-- parent's policy itself recurses to grandparent. For txn_legs the
-- chain was:
--
--     txn_legs.header_id   IN (SELECT id FROM txn_headers)
--   → txn_headers.ledger_id IN (SELECT ledger_id FROM user_ledger_grants
--                              WHERE user_id = current_app_user_id())
--   → user_ledger_grants.user_id = current_app_user_id()
--
-- And `holdings` / `lots` cascade through `accounts` / `txn_legs`
-- respectively, multiplying the work for every row scanned inside
-- the recompute function's inner loops.
--
-- THE STRUCTURAL FIX
--
-- Migration 049 already denormalized `ledger_id` onto every one of
-- these tables and locked it with composite FKs (e.g.
-- `txn_legs.(header_id, ledger_id) → txn_headers(id, ledger_id)`),
-- so it's impossible to insert a leg whose ledger_id disagrees with
-- its header's. That makes `ledger_id` a safe authority for RLS —
-- a single subquery against `user_ledger_grants` per scan instead
-- of a 2-3 level recursion.
--
-- Tables flattened here (each has `ledger_id` denormalized):
--   * txn_legs
--   * holdings
--   * lots
--   * security_prices
--   * security_splits
--
-- Tables NOT flattened (no denormalized `ledger_id`; their parent
-- already uses ledger_id directly, so the recursion is one layer
-- deep — fast enough):
--   * txn_header_overrides, txn_header_tags  → txn_headers
--   * txn_leg_overrides                      → txn_legs
--   * recurring_transactions                 → accounts
--   * sync_run_errors, sync_run_promotions   → sync_runs
--   * feed_connection_accounts               → feed_connections
--   * user_account_group_members             → accounts + user_account_groups
--
-- SECURITY EQUIVALENCE
--
-- The composite-FK invariant (049) means denormalized ledger_id is
-- always coherent with parent's ledger_id. So `ledger_id IN (visible
-- ledgers)` is exactly equivalent to `parent_fk IN (visible parent
-- rows)`. No security regression; just removes the recursion cost.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Helper: visible ledger set for the current request.
-- Inline the subquery in each policy rather than wrap in a function call;
-- Postgres caches the SELECT result per query when current_app_user_id()
-- is STABLE (which it is, declared in migration 017).
-- -----------------------------------------------------------------------------

-- txn_legs
DROP POLICY IF EXISTS txn_legs_per_user ON txn_legs;
CREATE POLICY txn_legs_per_user ON txn_legs
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

-- holdings
DROP POLICY IF EXISTS holdings_per_user ON holdings;
CREATE POLICY holdings_per_user ON holdings
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

-- lots
DROP POLICY IF EXISTS lots_per_user ON lots;
CREATE POLICY lots_per_user ON lots
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

-- security_prices
DROP POLICY IF EXISTS security_prices_per_user ON security_prices;
CREATE POLICY security_prices_per_user ON security_prices
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

-- security_splits
DROP POLICY IF EXISTS security_splits_per_user ON security_splits;
CREATE POLICY security_splits_per_user ON security_splits
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));
