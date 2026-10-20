-- =============================================================================
-- 073 — Composite index for the recompute hot-path + trigger early-exit
-- =============================================================================
--
-- TWO PREVENTIVE WINS
--
-- 1. Composite partial index on `txn_legs(account_id, security_id)`
--    where both `security_id IS NOT NULL` and `quantity IS NOT NULL`.
--    The recompute function's inner walk is:
--        WHERE l.security_id = X
--          AND l.account_id  = Y
--          AND l.quantity IS NOT NULL
--    Today Postgres uses `idx_txn_legs_security_id` then re-filters
--    by account_id + quantity. A single composite index that already
--    encodes the (account, security) pair + the quantity-not-null
--    filter is a direct match — saves planner work + buffer reads,
--    especially on brokerages with many securities.
--
-- 2. `trg_txn_legs_recompute_holdings()` early-exit. The trigger
--    fires on every txn_legs INSERT/UPDATE/DELETE statement — bank
--    INSERTs included, where every row has `security_id IS NULL`
--    and the function's DISTINCT walk returns zero pairs. Make the
--    exit explicit at the top via an EXISTS probe so the function
--    returns without scanning the whole transition table when
--    there's no qualifying row.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Part 1: composite partial index.
-- -----------------------------------------------------------------------------

CREATE INDEX IF NOT EXISTS idx_txn_legs_account_security_qty
    ON txn_legs (account_id, security_id)
    WHERE security_id IS NOT NULL AND quantity IS NOT NULL;

COMMENT ON INDEX idx_txn_legs_account_security_qty IS
    'Hot path for recompute_holdings_cost_basis (068+) inner walk: '
    'WHERE security_id = X AND account_id = Y AND quantity IS NOT NULL. '
    'Partial — only investment-shape legs qualify; bank legs ignored.';


-- -----------------------------------------------------------------------------
-- Part 2: explicit early-exit in the recompute trigger function.
--
-- Body is the same as 068 except for the leading EXISTS probe.
-- Returning before the FOR loop costs ~one transition-table scan
-- (in-memory) instead of building the DISTINCT result set.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION trg_txn_legs_recompute_holdings()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_pair RECORD;
BEGIN
    -- Early exit: bank-shape statements (no security_id on any
    -- row in the dirty_legs transition table) have no holding
    -- impact. Skip the DISTINCT/PERFORM dance entirely.
    IF NOT EXISTS (
        SELECT 1 FROM dirty_legs
        WHERE security_id IS NOT NULL AND quantity IS NOT NULL
    ) THEN
        RETURN NULL;
    END IF;

    FOR v_pair IN
        SELECT DISTINCT account_id, security_id
        FROM dirty_legs
        WHERE security_id IS NOT NULL AND quantity IS NOT NULL
    LOOP
        PERFORM recompute_holdings_cost_basis(
            NULL, v_pair.account_id, v_pair.security_id);
    END LOOP;

    RETURN NULL;
END;
$$;
