-- =============================================================================
-- 121 — Ledger isolation Phase 2 (remaining single-column cross-FKs; ADR-0020 / A3)
-- =============================================================================
--
-- WHY
--
-- Migration 049 (slice A3) closed the cross-ledger leakage gap on the
-- INVESTMENT surface (holdings, lots, security_prices, txn_legs) by
-- adopting **composite FKs**: every cross-table reference keys on
-- (parent_id, ledger_id) → parent(id, ledger_id), so PostgreSQL itself
-- refuses any INSERT/UPDATE that would point one ledger's row at another.
--
-- This migration completes that audit (follow-ups.md "Full-schema
-- ledger-isolation audit (Cross-FK Phase 2)") by applying the same
-- composite-FK pattern to the 7 remaining single-column FKs:
--
--   | child.col                              | parent           | on-delete |
--   |----------------------------------------|------------------|-----------|
--   | accounts.parent_id                     | accounts         | SET NULL  |
--   | accounts.holdings_account_id           | accounts         | SET NULL  |
--   | accounts.feed_connection_id            | feed_connections | SET NULL  |
--   | txn_headers.is_merged_into             | txn_headers      | SET NULL  |
--   | recurring_transactions.target_account_id | accounts       | SET NULL  |
--   | sync_runs.feed_connection_id           | feed_connections | SET NULL  |
--   | sync_run_promotions.header_id          | txn_headers      | CASCADE   |
--
-- VERIFIED PREREQUISITES (against the live dev DB — do NOT re-add):
--   * All 7 child tables already carry a NOT NULL `ledger_id`.
--   * All 3 parents (accounts, feed_connections, txn_headers) already
--     have UNIQUE (id, ledger_id) — accounts/txn_headers from mig 049,
--     feed_connections from its own anchor migration.
--   * All 7 references are currently LEAK-FREE (0 rows cross ledgers),
--     so the composite FKs add and validate cleanly with no data fix.
--
-- ON DELETE semantics are preserved exactly:
--   * The 6 nullable FKs keep SET-NULL. Because the FK is now composite,
--     a bare `ON DELETE SET NULL` would try to null BOTH columns
--     (parent_id AND ledger_id) — but ledger_id is NOT NULL, so the
--     delete would fail. PostgreSQL 15+ `ON DELETE SET NULL (<col>)`
--     scopes the SET NULL to the FK column only, leaving the row's
--     own NOT-NULL ledger_id intact. That's exactly what we want: a
--     parent delete clears the dangling reference without disturbing
--     the child's ledger membership.
--   * sync_run_promotions.header_id stays CASCADE (a promotion is
--     meaningless once its header is gone).
--
-- New constraints are named `<table>_<col>_ledger_fkey`, mirroring
-- mig 049's `<table>_<col>_fkey` composite naming with a `_ledger`
-- marker so the composite intent is legible in \d output.
--
-- SUPPORTING INDEXES. A composite FK does not REQUIRE an index on the
-- referencing columns, but one helps the SET NULL / CASCADE scan that
-- runs on a parent delete. Index support already exists for 6 of the
-- 7 FK columns:
--   * accounts.parent_id / holdings_account_id / feed_connection_id —
--     partial single-col indexes from mig 120.
--   * txn_headers.is_merged_into — idx_txn_headers_is_merged_into (mig 022).
--   * sync_runs.feed_connection_id — leading col of
--     idx_sync_runs_feed_connection_started (mig 038).
--   * sync_run_promotions.header_id — idx_sync_run_promotions_header (mig 038).
-- The one gap is recurring_transactions.target_account_id (mig 120 added
-- an index on source_account_id, NOT target). This migration adds a
-- composite partial index there so the SET NULL scan on an account
-- delete is index-supported like the rest.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1. accounts.parent_id → accounts(id, ledger_id)  ON DELETE SET NULL (parent_id)
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    DROP CONSTRAINT accounts_parent_id_fkey,
    ADD CONSTRAINT accounts_parent_id_ledger_fkey
        FOREIGN KEY (parent_id, ledger_id) REFERENCES accounts(id, ledger_id)
        ON DELETE SET NULL (parent_id);

-- ---------------------------------------------------------------------------
-- 2. accounts.holdings_account_id → accounts(id, ledger_id)  ON DELETE SET NULL (holdings_account_id)
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    DROP CONSTRAINT accounts_holdings_account_id_fkey,
    ADD CONSTRAINT accounts_holdings_account_id_ledger_fkey
        FOREIGN KEY (holdings_account_id, ledger_id) REFERENCES accounts(id, ledger_id)
        ON DELETE SET NULL (holdings_account_id);

-- ---------------------------------------------------------------------------
-- 3. accounts.feed_connection_id → feed_connections(id, ledger_id)  ON DELETE SET NULL (feed_connection_id)
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    DROP CONSTRAINT accounts_feed_connection_id_fkey,
    ADD CONSTRAINT accounts_feed_connection_id_ledger_fkey
        FOREIGN KEY (feed_connection_id, ledger_id) REFERENCES feed_connections(id, ledger_id)
        ON DELETE SET NULL (feed_connection_id);

-- ---------------------------------------------------------------------------
-- 4. txn_headers.is_merged_into → txn_headers(id, ledger_id)  ON DELETE SET NULL (is_merged_into)
-- ---------------------------------------------------------------------------
-- Self-referential: a header points at the winner it was merged into.
ALTER TABLE txn_headers
    DROP CONSTRAINT txn_headers_is_merged_into_fkey,
    ADD CONSTRAINT txn_headers_is_merged_into_ledger_fkey
        FOREIGN KEY (is_merged_into, ledger_id) REFERENCES txn_headers(id, ledger_id)
        ON DELETE SET NULL (is_merged_into);

-- ---------------------------------------------------------------------------
-- 5. recurring_transactions.target_account_id → accounts(id, ledger_id)  ON DELETE SET NULL (target_account_id)
-- ---------------------------------------------------------------------------
ALTER TABLE recurring_transactions
    DROP CONSTRAINT recurring_transactions_target_account_id_fkey,
    ADD CONSTRAINT recurring_transactions_target_account_id_ledger_fkey
        FOREIGN KEY (target_account_id, ledger_id) REFERENCES accounts(id, ledger_id)
        ON DELETE SET NULL (target_account_id);

-- Index gap (see header): mig 120 indexed source_account_id but not
-- target. Composite (target_account_id, ledger_id), partial on the
-- populated rows — target_account_id is nullable and mostly NULL.
CREATE INDEX IF NOT EXISTS idx_recurring_transactions_target_account_id_ledger
    ON recurring_transactions (target_account_id, ledger_id)
    WHERE target_account_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 6. sync_runs.feed_connection_id → feed_connections(id, ledger_id)  ON DELETE SET NULL (feed_connection_id)
-- ---------------------------------------------------------------------------
ALTER TABLE sync_runs
    DROP CONSTRAINT sync_runs_feed_connection_id_fkey,
    ADD CONSTRAINT sync_runs_feed_connection_id_ledger_fkey
        FOREIGN KEY (feed_connection_id, ledger_id) REFERENCES feed_connections(id, ledger_id)
        ON DELETE SET NULL (feed_connection_id);

-- ---------------------------------------------------------------------------
-- 7. sync_run_promotions.header_id → txn_headers(id, ledger_id)  ON DELETE CASCADE
-- ---------------------------------------------------------------------------
ALTER TABLE sync_run_promotions
    DROP CONSTRAINT sync_run_promotions_header_id_fkey,
    ADD CONSTRAINT sync_run_promotions_header_id_ledger_fkey
        FOREIGN KEY (header_id, ledger_id) REFERENCES txn_headers(id, ledger_id)
        ON DELETE CASCADE;

-- ---------------------------------------------------------------------------
-- 8. Verification — every referencing row's ledger_id agrees with its parent.
-- ---------------------------------------------------------------------------
-- The composite FKs would have rejected the migration had any row
-- violated; the explicit COUNT makes the migration log say "0
-- mismatches" so future-us doesn't wonder. (All NULL FK columns are
-- excluded — a NULL composite key is unenforced under MATCH SIMPLE.)
DO $$
DECLARE
    bad_parent    INTEGER;
    bad_holdings  INTEGER;
    bad_acct_feed INTEGER;
    bad_merged    INTEGER;
    bad_recur     INTEGER;
    bad_run_feed  INTEGER;
    bad_promo     INTEGER;
    total         INTEGER;
BEGIN
    SELECT COUNT(*) INTO bad_parent FROM accounts c
        JOIN accounts p ON p.id = c.parent_id
        WHERE c.parent_id IS NOT NULL AND p.ledger_id <> c.ledger_id;
    SELECT COUNT(*) INTO bad_holdings FROM accounts c
        JOIN accounts p ON p.id = c.holdings_account_id
        WHERE c.holdings_account_id IS NOT NULL AND p.ledger_id <> c.ledger_id;
    SELECT COUNT(*) INTO bad_acct_feed FROM accounts c
        JOIN feed_connections f ON f.id = c.feed_connection_id
        WHERE c.feed_connection_id IS NOT NULL AND f.ledger_id <> c.ledger_id;
    SELECT COUNT(*) INTO bad_merged FROM txn_headers c
        JOIN txn_headers p ON p.id = c.is_merged_into
        WHERE c.is_merged_into IS NOT NULL AND p.ledger_id <> c.ledger_id;
    SELECT COUNT(*) INTO bad_recur FROM recurring_transactions c
        JOIN accounts a ON a.id = c.target_account_id
        WHERE c.target_account_id IS NOT NULL AND a.ledger_id <> c.ledger_id;
    SELECT COUNT(*) INTO bad_run_feed FROM sync_runs c
        JOIN feed_connections f ON f.id = c.feed_connection_id
        WHERE c.feed_connection_id IS NOT NULL AND f.ledger_id <> c.ledger_id;
    SELECT COUNT(*) INTO bad_promo FROM sync_run_promotions c
        JOIN txn_headers h ON h.id = c.header_id
        WHERE h.ledger_id <> c.ledger_id;

    total := bad_parent + bad_holdings + bad_acct_feed + bad_merged
           + bad_recur + bad_run_feed + bad_promo;

    RAISE NOTICE 'Migration 121 verification: parent=% holdings=% acct_feed=% merged=% recurring=% run_feed=% promo=% (all should be 0)',
        bad_parent, bad_holdings, bad_acct_feed, bad_merged, bad_recur, bad_run_feed, bad_promo;

    IF total > 0 THEN
        RAISE EXCEPTION 'Migration 121 found % cross-ledger reference(s) — composite FK would reject. Halt.', total;
    END IF;
END $$;
