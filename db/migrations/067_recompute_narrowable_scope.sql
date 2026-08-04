-- =============================================================================
-- 067 — recompute_holdings_cost_basis: narrowable to (account, security)
-- =============================================================================
--
-- THE PERF PROBLEM
--
-- A4.c.3's InvestmentTxnRowEdit save flow calls
-- `recompute_holdings_cost_basis(ledger_id)` after every POST / PATCH /
-- DELETE. The function walks EVERY holding in the ledger — for a
-- brokerage with ~20 holdings each save took ~2.2 s end-to-end. The
-- recompute itself dominated (per-holding outer loop × UNION ALL over
-- txn_legs + security_splits + per-lot FIFO updates).
--
-- A single new investment txn affects ONE holding key: (brokerage,
-- security). Walking the other 19 is pure waste.
--
-- THE FIX
--
-- Add two optional parameters — `p_account_id` (brokerage) and
-- `p_security_id` — that narrow the outer FOR loop. Both default to
-- NULL so existing call sites (importer's end-of-import scrub, this
-- migration's safety scrub) continue to walk the whole ledger.
--
-- New callers in the API repository (slice A4.c.3 perf fix) pass both
-- so a save recomputes the single affected holding. Target: ~100-200 ms
-- per save instead of ~2.2 s.
--
-- COMPATIBILITY
--
-- The old 1-arg signature (`recompute_holdings_cost_basis(UUID)`) must
-- be DROPped first; Postgres doesn't allow CREATE OR REPLACE to change
-- the parameter count. The new signature's `p_ledger_id UUID DEFAULT
-- NULL` covers the 1-arg call shape: `SELECT
-- recompute_holdings_cost_basis(some_ledger_id)` still resolves and
-- executes identically to the migration-063 version when the other
-- params are NULL.
--
-- The function body is otherwise identical to 063 — only the outer
-- WHERE clause gains the two new filters.
-- =============================================================================

DROP FUNCTION IF EXISTS recompute_holdings_cost_basis(UUID);

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(
    p_ledger_id   UUID DEFAULT NULL,
    p_account_id  UUID DEFAULT NULL,
    p_security_id UUID DEFAULT NULL
)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_event RECORD;
    v_lot RECORD;
    v_brokerage_include_fees BOOLEAN;
    v_running_qty NUMERIC;
    v_running_basis NUMERIC;
    v_avg_cost NUMERIC;
    v_fee NUMERIC;
    v_remaining_sell NUMERIC;
    v_updated INTEGER := 0;
BEGIN
    FOR v_holding IN
        SELECT id, account_id, security_id, ledger_id
        FROM holdings
        WHERE (p_ledger_id   IS NULL OR ledger_id   = p_ledger_id)
          -- Narrowing parameters added in 067. holdings.account_id is
          -- the BROKERAGE id (not the Holdings sibling); callers in
          -- InvestmentTransactionsRepository pass the request's
          -- brokerageAccountId here.
          AND (p_account_id  IS NULL OR account_id  = p_account_id)
          AND (p_security_id IS NULL OR security_id = p_security_id)
    LOOP
        SELECT COALESCE(b.is_trade_commission, FALSE)
        INTO v_brokerage_include_fees
        FROM accounts b
        WHERE b.holdings_account_id = v_holding.account_id;
        v_brokerage_include_fees := COALESCE(v_brokerage_include_fees, FALSE);

        -- Lot reset. Zero-qty guard matches the C# mapper convention.
        UPDATE lots l
        SET quantity  = tl.quantity,
            is_closed = FALSE,
            unit_cost = CASE
                WHEN tl.quantity = 0 THEN 0
                WHEN v_brokerage_include_fees THEN
                    (tl.amount + COALESCE((
                        SELECT SUM(fl.amount)
                        FROM txn_legs fl
                        WHERE fl.header_id    = tl.header_id
                          AND fl.posting_role = 'fee'
                          AND fl.amount > 0
                    ), 0)) / tl.quantity
                ELSE
                    tl.amount / tl.quantity
            END
        FROM txn_legs tl
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id;

        v_running_qty   := 0;
        v_running_basis := 0;

        FOR v_event IN
            SELECT
                'leg'::TEXT AS kind,
                hd.posted_at AS event_at,
                l.id AS leg_id,
                l.header_id,
                l.amount,
                l.quantity,
                NULL::NUMERIC AS ratio
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL

            UNION ALL

            SELECT
                'split'::TEXT AS kind,
                ss.split_at AS event_at,
                NULL::UUID AS leg_id,
                NULL::UUID AS header_id,
                NULL::NUMERIC AS amount,
                NULL::NUMERIC AS quantity,
                ss.ratio
            FROM security_splits ss
            WHERE ss.security_id = v_holding.security_id
              AND ss.ledger_id   = v_holding.ledger_id

            ORDER BY event_at, kind, leg_id
        LOOP
            IF v_event.kind = 'split' THEN
                v_running_qty := v_running_qty * v_event.ratio;

                UPDATE lots
                SET quantity = quantity * v_event.ratio
                WHERE holding_id = v_holding.id
                  AND is_closed  = FALSE;

            ELSIF v_event.quantity > 0 THEN
                IF v_brokerage_include_fees THEN
                    v_fee := COALESCE((
                        SELECT SUM(fl.amount)
                        FROM txn_legs fl
                        WHERE fl.header_id    = v_event.header_id
                          AND fl.posting_role = 'fee'
                          AND fl.amount > 0
                    ), 0);
                ELSE
                    v_fee := 0;
                END IF;
                v_running_qty   := v_running_qty + v_event.quantity;
                v_running_basis := v_running_basis + v_event.amount + v_fee;

            ELSIF v_event.quantity < 0 AND v_running_qty > 0 THEN
                v_avg_cost      := v_running_basis / v_running_qty;
                v_running_basis := v_running_basis - (v_avg_cost * ABS(v_event.quantity));
                v_running_qty   := v_running_qty + v_event.quantity;
                IF v_running_qty <= 0 THEN
                    v_running_qty   := 0;
                    v_running_basis := 0;
                END IF;

                v_remaining_sell := ABS(v_event.quantity);
                FOR v_lot IN
                    SELECT id, quantity
                    FROM lots
                    WHERE holding_id = v_holding.id
                      AND is_closed  = FALSE
                      AND quantity   > 0
                    ORDER BY acquired_at, id
                LOOP
                    EXIT WHEN v_remaining_sell <= 0;

                    IF v_lot.quantity <= v_remaining_sell THEN
                        UPDATE lots
                        SET quantity  = 0,
                            is_closed = TRUE
                        WHERE id = v_lot.id;
                        v_remaining_sell := v_remaining_sell - v_lot.quantity;
                    ELSE
                        UPDATE lots
                        SET quantity = quantity - v_remaining_sell
                        WHERE id = v_lot.id;
                        v_remaining_sell := 0;
                    END IF;
                END LOOP;
            END IF;
        END LOOP;

        UPDATE holdings
        SET cost_basis = v_running_basis,
            quantity   = v_running_qty
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$$;

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID, UUID, UUID) IS
    'Walks the unified (txn_legs union security_splits) event stream per holding '
    'in chronological order (migration 060). Reads the brokerage''s '
    'is_trade_commission flag (056) to decide whether posting_role=''fee'' '
    'amounts flow into basis. Increments basis + qty on Buy / DivReinvest, '
    'reduces both on Sell via avg-cost + FIFO lot closure, and multiplies '
    'running_qty + every open lot by ratio on stock splits. Authoritative '
    'writer of holdings.quantity and holdings.cost_basis from 060 onward. '
    'Migration 063 added a zero-qty guard in the lot reset. Migration 067 '
    'added p_account_id + p_security_id narrowing parameters (both default '
    'NULL = walk everything) so per-save recomputes touch only the affected '
    'holding instead of the whole ledger (~10-20x speedup on N-holding '
    'brokerages).';
