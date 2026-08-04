-- =============================================================================
-- 117 — recompute_holdings_cost_basis: exclude hidden headers
-- =============================================================================
--
-- ROOT CAUSE
--
-- `txn_headers.is_hidden = true` is the soft-delete marker for
-- import-keyed rows (ADR-0023 §Delete). When the user clicks Delete
-- in the SPA on a row that has a non-null `external_id`, the server
-- sets `is_hidden = true` instead of hard-deleting — preserving the
-- raw row so a subsequent re-source doesn't resurrect it.
--
-- Mig 103 made the BALANCE recompute exclude hidden headers (a
-- soft-deleted txn's signed amount must not affect the running
-- cash balance). The HOLDINGS recompute (mig 067 / 068, just
-- updated by mig 116) does not. Result: a soft-deleted investment
-- transaction still contributes its quantity to
-- `holdings.quantity` and cost basis. Indistinguishable from a
-- "real" position from the API's read path.
--
-- Real-data evidence: a "Sample transaction" DivReinvest in the
-- user's MD JSON (a manual test entry, no OFX provenance) was
-- soft-deleted in the SPA. Holdings still reflected its
-- +5.000000000 shares of FUNDX, accounting for the entire
-- discrepancy between Coffer (5.00 shares) and the broker
-- (0 shares).
--
-- THE FIX
--
-- Add `hd.is_hidden = false` to the leg walk's WHERE clause. A
-- header soft-hidden via the API stops affecting holdings on the
-- next recompute. Resurrecting a header (clearing is_hidden) puts
-- it back in scope. Same contract as the balance recompute.
--
-- Security splits are unaffected: they're not header-shaped (the
-- `security_splits` table has no `is_hidden`); a stock-split event
-- that affected past holdings continues to apply regardless.
--
-- ONE-SHOT REPAIR
--
-- Existing data was computed without the hidden-filter. Re-walk
-- every holding so stored quantity + cost_basis converge to the
-- new contract.
-- =============================================================================

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
    v_resolved_ledger_id UUID;
BEGIN
    -- Auto-create the holding row when the caller pinned a specific
    -- (account, security) but no row exists yet (mig 068).
    IF p_account_id IS NOT NULL AND p_security_id IS NOT NULL THEN
        SELECT ledger_id INTO v_resolved_ledger_id
        FROM accounts WHERE id = p_account_id;

        IF v_resolved_ledger_id IS NOT NULL
           AND (p_ledger_id IS NULL OR p_ledger_id = v_resolved_ledger_id)
           AND NOT EXISTS (
               SELECT 1 FROM holdings
               WHERE account_id  = p_account_id
                 AND security_id = p_security_id
           )
        THEN
            INSERT INTO holdings (id, account_id, security_id, ledger_id, quantity, cost_basis, as_of)
            VALUES (gen_random_uuid(), p_account_id, p_security_id, v_resolved_ledger_id, 0, 0, NOW());
        END IF;
    END IF;

    FOR v_holding IN
        SELECT id, account_id, security_id, ledger_id
        FROM holdings
        WHERE (p_ledger_id   IS NULL OR ledger_id   = p_ledger_id)
          AND (p_account_id  IS NULL OR account_id  = p_account_id)
          AND (p_security_id IS NULL OR security_id = p_security_id)
    LOOP
        SELECT COALESCE(b.is_trade_commission, FALSE)
        INTO v_brokerage_include_fees
        FROM accounts b
        WHERE b.holdings_account_id = v_holding.account_id;
        v_brokerage_include_fees := COALESCE(v_brokerage_include_fees, FALSE);

        -- Lot reset. Mig 117: skip hidden headers' legs — their lots
        -- should not be touched by the recompute (the soft-deleted
        -- txn no longer participates).
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
        JOIN txn_headers th ON th.id = tl.header_id
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id
          AND th.is_hidden = FALSE;

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
              -- Mig 117 (ADR-0040): soft-deleted headers (is_hidden = true)
              -- no longer contribute to holdings. Same contract as the
              -- balance recompute (mig 103). Hidden via the SPA Delete
              -- flow on import-keyed rows; hide is the canonical
              -- soft-delete marker per ADR-0023.
              AND hd.is_hidden = FALSE

            UNION ALL

            -- Splits aren't header-shaped — no is_hidden filter applies.
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

            ELSIF v_event.quantity < 0 THEN
                IF v_running_qty > 0 THEN
                    v_avg_cost := v_running_basis / v_running_qty;
                    v_running_basis := v_running_basis
                        - (v_avg_cost * LEAST(v_running_qty, ABS(v_event.quantity)));
                END IF;
                v_running_qty := v_running_qty + v_event.quantity;

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
    'Walks the unified (txn_legs union security_splits) event stream per '
    'holding in chronological order. Mig 116 dropped the quantity clamp '
    '(ADR-0039: pure running sum, order-independent). Mig 117 excludes '
    'soft-hidden headers (ADR-0040: parity with balance recompute mig 103).';

-- One-shot repair under the new contract.
SELECT recompute_holdings_cost_basis();
