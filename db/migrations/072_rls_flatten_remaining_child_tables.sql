-- =============================================================================
-- 072 — Denormalize ledger_id onto remaining child tables; flatten RLS
-- =============================================================================
--
-- Migration 071 flattened the 5 hot-path RLS policies (txn_legs,
-- holdings, lots, security_prices, security_splits) that were
-- multiplying recompute cost 180×. This migration applies the same
-- pattern to the 8 remaining child tables whose policies still use
-- 1-layer parent recursion. The current cost is small (parent uses
-- ledger_id directly, so each row check evaluates one subquery
-- instead of cascading), but the inconsistency is its own problem:
-- the next developer adding a heavy query against one of these
-- tables will hit the same shape of issue.
--
-- Same security guarantee as 071: composite FK enforces that
-- denormalized ledger_id stays coherent with parent's ledger_id, so
-- `ledger_id IN (visible ledgers)` is exactly equivalent to
-- `parent_fk IN (visible parent rows)`.
--
-- Affected tables:
--   * txn_header_overrides        — parent: txn_headers
--   * txn_header_tags             — parent: txn_headers (also references tags)
--   * txn_leg_overrides           — parent: txn_legs
--   * recurring_transactions      — parent: accounts (source_account_id)
--   * sync_run_errors             — parent: sync_runs
--   * sync_run_promotions         — parent: sync_runs
--   * feed_connection_accounts    — parent: feed_connections
--   * user_account_group_members  — parent: user_account_groups
--
-- Parents that need composite UNIQUE(id, ledger_id) added so children
-- can reference them via composite FK (only 5 of the 9 parents had
-- this from migration 049):
--   * tags, sync_runs, feed_connections, user_account_groups
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Part 1: parent composite UNIQUE constraints.
-- -----------------------------------------------------------------------------

ALTER TABLE tags                ADD CONSTRAINT uq_tags_id_ledger                UNIQUE (id, ledger_id);
ALTER TABLE sync_runs           ADD CONSTRAINT uq_sync_runs_id_ledger           UNIQUE (id, ledger_id);
ALTER TABLE feed_connections    ADD CONSTRAINT uq_feed_connections_id_ledger    UNIQUE (id, ledger_id);
ALTER TABLE user_account_groups ADD CONSTRAINT uq_user_account_groups_id_ledger UNIQUE (id, ledger_id);


-- -----------------------------------------------------------------------------
-- Part 2: add + backfill + lock ledger_id on each child table.
--
-- Pattern per table:
--   1. ALTER TABLE ADD COLUMN ledger_id UUID (nullable initially)
--   2. UPDATE with backfill from parent
--   3. ALTER COLUMN SET NOT NULL
--   4. Add composite FK to parent (enforces coherence)
--   5. DROP + CREATE RLS policy to use ledger_id directly
-- -----------------------------------------------------------------------------

-- txn_header_overrides → txn_headers
ALTER TABLE txn_header_overrides ADD COLUMN ledger_id UUID;
UPDATE txn_header_overrides o SET ledger_id = h.ledger_id
  FROM txn_headers h WHERE h.id = o.header_id;
ALTER TABLE txn_header_overrides ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE txn_header_overrides ADD CONSTRAINT txn_header_overrides_header_ledger_fkey
  FOREIGN KEY (header_id, ledger_id) REFERENCES txn_headers(id, ledger_id) ON DELETE CASCADE;

DROP POLICY IF EXISTS txn_header_overrides_per_user ON txn_header_overrides;
CREATE POLICY txn_header_overrides_per_user ON txn_header_overrides
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- txn_header_tags → txn_headers + tags (both must share the row's ledger_id)
ALTER TABLE txn_header_tags ADD COLUMN ledger_id UUID;
UPDATE txn_header_tags t SET ledger_id = h.ledger_id
  FROM txn_headers h WHERE h.id = t.header_id;
ALTER TABLE txn_header_tags ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE txn_header_tags ADD CONSTRAINT txn_header_tags_header_ledger_fkey
  FOREIGN KEY (header_id, ledger_id) REFERENCES txn_headers(id, ledger_id) ON DELETE CASCADE;
ALTER TABLE txn_header_tags ADD CONSTRAINT txn_header_tags_tag_ledger_fkey
  FOREIGN KEY (tag_id, ledger_id) REFERENCES tags(id, ledger_id) ON DELETE CASCADE;

DROP POLICY IF EXISTS txn_header_tags_per_user ON txn_header_tags;
CREATE POLICY txn_header_tags_per_user ON txn_header_tags
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- txn_leg_overrides → txn_legs (txn_legs.ledger_id is the authority post-049)
ALTER TABLE txn_leg_overrides ADD COLUMN ledger_id UUID;
UPDATE txn_leg_overrides o SET ledger_id = l.ledger_id
  FROM txn_legs l WHERE l.id = o.leg_id;
ALTER TABLE txn_leg_overrides ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE txn_leg_overrides ADD CONSTRAINT txn_leg_overrides_leg_ledger_fkey
  FOREIGN KEY (leg_id, ledger_id) REFERENCES txn_legs(id, ledger_id) ON DELETE CASCADE;

DROP POLICY IF EXISTS txn_leg_overrides_per_user ON txn_leg_overrides;
CREATE POLICY txn_leg_overrides_per_user ON txn_leg_overrides
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- recurring_transactions → accounts (via source_account_id)
-- target_account_id can be NULL or in a different ledger — RLS gates on
-- source ledger only, matching the existing policy's intent.
ALTER TABLE recurring_transactions ADD COLUMN ledger_id UUID;
UPDATE recurring_transactions r SET ledger_id = a.ledger_id
  FROM accounts a WHERE a.id = r.source_account_id;
ALTER TABLE recurring_transactions ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE recurring_transactions ADD CONSTRAINT recurring_transactions_source_account_ledger_fkey
  FOREIGN KEY (source_account_id, ledger_id) REFERENCES accounts(id, ledger_id) ON DELETE RESTRICT;

DROP POLICY IF EXISTS recurring_transactions_per_user ON recurring_transactions;
CREATE POLICY recurring_transactions_per_user ON recurring_transactions
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- sync_run_errors → sync_runs
ALTER TABLE sync_run_errors ADD COLUMN ledger_id UUID;
UPDATE sync_run_errors e SET ledger_id = sr.ledger_id
  FROM sync_runs sr WHERE sr.id = e.sync_run_id;
ALTER TABLE sync_run_errors ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE sync_run_errors ADD CONSTRAINT sync_run_errors_run_ledger_fkey
  FOREIGN KEY (sync_run_id, ledger_id) REFERENCES sync_runs(id, ledger_id) ON DELETE CASCADE;

DROP POLICY IF EXISTS sync_run_errors_per_user ON sync_run_errors;
CREATE POLICY sync_run_errors_per_user ON sync_run_errors
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- sync_run_promotions → sync_runs
ALTER TABLE sync_run_promotions ADD COLUMN ledger_id UUID;
UPDATE sync_run_promotions p SET ledger_id = sr.ledger_id
  FROM sync_runs sr WHERE sr.id = p.sync_run_id;
ALTER TABLE sync_run_promotions ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE sync_run_promotions ADD CONSTRAINT sync_run_promotions_run_ledger_fkey
  FOREIGN KEY (sync_run_id, ledger_id) REFERENCES sync_runs(id, ledger_id) ON DELETE CASCADE;

DROP POLICY IF EXISTS sync_run_promotions_per_user ON sync_run_promotions;
CREATE POLICY sync_run_promotions_per_user ON sync_run_promotions
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- feed_connection_accounts → feed_connections
ALTER TABLE feed_connection_accounts ADD COLUMN ledger_id UUID;
UPDATE feed_connection_accounts a SET ledger_id = fc.ledger_id
  FROM feed_connections fc WHERE fc.id = a.feed_connection_id;
ALTER TABLE feed_connection_accounts ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE feed_connection_accounts ADD CONSTRAINT feed_connection_accounts_connection_ledger_fkey
  FOREIGN KEY (feed_connection_id, ledger_id) REFERENCES feed_connections(id, ledger_id) ON DELETE CASCADE;

DROP POLICY IF EXISTS feed_connection_accounts_per_user ON feed_connection_accounts;
CREATE POLICY feed_connection_accounts_per_user ON feed_connection_accounts
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- user_account_group_members → user_account_groups + accounts
-- The original policy AND'd two subqueries (group + account). Keep
-- both ledger checks: members carry the group's ledger_id, and we
-- additionally verify the account ledger matches via composite FK.
ALTER TABLE user_account_group_members ADD COLUMN ledger_id UUID;
UPDATE user_account_group_members m SET ledger_id = g.ledger_id
  FROM user_account_groups g WHERE g.id = m.group_id;
ALTER TABLE user_account_group_members ALTER COLUMN ledger_id SET NOT NULL;
ALTER TABLE user_account_group_members ADD CONSTRAINT user_account_group_members_group_ledger_fkey
  FOREIGN KEY (group_id, ledger_id) REFERENCES user_account_groups(id, ledger_id) ON DELETE CASCADE;
ALTER TABLE user_account_group_members ADD CONSTRAINT user_account_group_members_account_ledger_fkey
  FOREIGN KEY (account_id, ledger_id) REFERENCES accounts(id, ledger_id) ON DELETE CASCADE;

DROP POLICY IF EXISTS user_account_group_members_self ON user_account_group_members;
CREATE POLICY user_account_group_members_self ON user_account_group_members
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));
