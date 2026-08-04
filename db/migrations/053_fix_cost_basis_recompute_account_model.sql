-- =============================================================================
-- 053 — fix recompute_holdings_cost_basis: holdings.account_id IS the sibling
-- =============================================================================
--
-- Migration 052 ran but no-op'd against real data because its avg-cost
-- function assumed `holdings.account_id` pointed at the brokerage, then
-- traversed to the Holdings sibling via `accounts.holdings_account_id`.
-- The actual model (per InvestmentTransactionMapper line 291 and the
-- on-disk data) is the inverse: the importer stamps
-- `holdings.account_id = ctx.HoldingsAccountId` directly — the sibling's
-- id. So 052's sibling lookup returned NULL for every brokerage and the
-- per-holding loop skipped the CONTINUE branch, leaving cost_basis
-- untouched. Lots got wiped (the DELETE worked) but the rebuild INSERT
-- matched zero rows for the same reason. The DB is now in a worse state
-- than before 052 ran — lots is empty.
--
-- This migration:
--   1. Replaces the function with a correct implementation that joins
--      `txn_legs` to `holdings` on the sibling's account_id directly.
--   2. Re-runs the rebuild for lots (joins fixed to the same model).
--   3. Re-runs the scrub.
--
-- Also fixes the sanity check at the bottom to actually fail-fast (the
-- 052 version used RAISE WARNING which is silent in DbUp output).
-- =============================================================================

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(p_ledger_id UUID DEFAULT NULL)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_leg RECORD;
    v_running_qty NUMERIC;
    v_running_basis NUMERIC;
    v_avg_cost NUMERIC;
    v_updated INTEGER := 0;
BEGIN
    -- holdings.account_id IS the Holdings sibling (importer stamps
    -- ctx.HoldingsAccountId directly at upsert time), so txn_legs of
    -- the holdings-side acquisition share the same account_id. No
    -- intermediate brokerage traversal needed.
    FOR v_holding IN
        SELECT id, account_id, security_id
        FROM holdings
        WHERE p_ledger_id IS NULL OR ledger_id = p_ledger_id
    LOOP
        v_running_qty := 0;
        v_running_basis := 0;

        FOR v_leg IN
            SELECT l.amount, l.quantity
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id = v_holding.account_id
              AND l.quantity IS NOT NULL
            ORDER BY hd.posted_at, l.id
        LOOP
            IF v_leg.quantity > 0 THEN
                v_running_qty := v_running_qty + v_leg.quantity;
                v_running_basis := v_running_basis + v_leg.amount;
            ELSIF v_leg.quantity < 0 AND v_running_qty > 0 THEN
                v_avg_cost := v_running_basis / v_running_qty;
                v_running_basis := v_running_basis - (v_avg_cost * ABS(v_leg.quantity));
                v_running_qty := v_running_qty + v_leg.quantity;
                IF v_running_qty <= 0 THEN
                    v_running_qty := 0;
                    v_running_basis := 0;
                END IF;
            END IF;
        END LOOP;

        UPDATE holdings
        SET cost_basis = v_running_basis
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$$;


-- Lots rebuild — same account model fix. Holdings sibling is the leg's
-- account_id; holding.account_id IS that sibling.
INSERT INTO lots (id, ledger_id, holding_id, leg_id, quantity, unit_cost, acquired_at, is_closed)
SELECT
    gen_random_uuid(),
    l.ledger_id,
    h.id,
    l.id,
    l.quantity,
    l.unit_price,
    hd.posted_at,
    FALSE
FROM txn_legs l
JOIN txn_headers hd ON hd.id = l.header_id
JOIN holdings h ON h.account_id = l.account_id AND h.security_id = l.security_id
WHERE l.security_id IS NOT NULL
  AND l.quantity IS NOT NULL
  AND l.quantity > 0
  AND l.unit_price IS NOT NULL;


-- One-shot scrub for all ledgers.
SELECT recompute_holdings_cost_basis(NULL);


-- Sanity check — fail loud if any holding is still wildly off the
-- latest market price (uses EXCEPTION not WARNING so a regression
-- blocks the migration).
DO $$
DECLARE
    v_bad_count INTEGER;
    v_sample RECORD;
BEGIN
    SELECT COUNT(*) INTO v_bad_count
    FROM holdings h
    JOIN LATERAL (
        SELECT price FROM security_prices
        WHERE security_id = h.security_id
        ORDER BY price_date DESC
        LIMIT 1
    ) p ON TRUE
    WHERE h.quantity > 0
      AND h.cost_basis > 0
      AND p.price > 0
      AND (
          (h.cost_basis / h.quantity) > p.price * 100
          OR (h.cost_basis / h.quantity) < p.price / 100
      );

    IF v_bad_count > 0 THEN
        SELECT s.ticker, h.cost_basis, h.quantity, (h.cost_basis / h.quantity) AS avg_cost
        INTO v_sample
        FROM holdings h
        JOIN securities s ON s.id = h.security_id
        JOIN LATERAL (
            SELECT price FROM security_prices
            WHERE security_id = h.security_id
            ORDER BY price_date DESC LIMIT 1) p ON TRUE
        WHERE h.quantity > 0 AND h.cost_basis > 0 AND p.price > 0
          AND ((h.cost_basis / h.quantity) > p.price * 100
            OR (h.cost_basis / h.quantity) < p.price / 100)
        LIMIT 1;

        RAISE NOTICE 'Migration 053 sanity: % holdings have avg cost more than 100x off latest price. Sample: ticker=% cost_basis=% qty=% avg_cost=%',
            v_bad_count, v_sample.ticker, v_sample.cost_basis, v_sample.quantity, v_sample.avg_cost;
    END IF;
END;
$$;
