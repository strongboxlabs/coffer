-- =============================================================================
-- 063 — recompute_holdings_cost_basis: zero-quantity guard in lot reset
-- =============================================================================
--
-- The lot-reset block (introduced in 056, carried forward in 060)
-- pulls a lot's source leg from txn_legs and divides tl.amount by
-- tl.quantity to produce the lot's unit_cost. Acquisition legs
-- normally carry quantity > 0, but MD's real data has edge-case
-- divr / buy events with `sec.samt = 0` — a degenerate "dividend
-- of zero shares" or "buy of zero shares" that MD permits as a
-- bookkeeping side-effect of OFX import paths.
--
-- The C# mapper guards against this in `ComputeUnitPrice` (returns
-- 0 when qty == 0) and in `LotRow.UnitCost` initialization (same
-- guard), so the persisted lot.unit_cost is 0 for these rows. The
-- SQL function should match the C# convention.
--
-- This migration replaces the function with a version that wraps
-- both arms of the CASE in a zero-qty guard. Behavior is identical
-- for every lot with quantity > 0 (the common case).
-- =============================================================================

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(p_ledger_id UUID DEFAULT NULL)
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
        WHERE p_ledger_id IS NULL OR ledger_id = p_ledger_id
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

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID) IS
    'Walks the unified (txn_legs ∪ security_splits) event stream per holding '
    'in chronological order (migration 060). Reads the brokerage''s '
    'is_trade_commission flag (056) to decide whether posting_role=''fee'' '
    'amounts flow into basis. Increments basis + qty on Buy / DivReinvest, '
    'reduces both on Sell via avg-cost + FIFO lot closure, and multiplies '
    'running_qty + every open lot by ratio on stock splits. Authoritative '
    'writer of holdings.quantity and holdings.cost_basis from 060 onward. '
    'Migration 063 added a zero-qty guard in the lot reset to match the C# '
    'mapper''s ComputeUnitPrice convention (degenerate MD divr/buy with '
    'sec.samt=0).';
