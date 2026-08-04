-- =============================================================================
-- 118 — recompute_holdings_cost_basis: deterministic intra-day event order
-- =============================================================================
--
-- ROOT CAUSE
--
-- Mig 067 / 068 / 116 / 117 left the event-stream sort as
-- `ORDER BY event_at, kind, leg_id`. Two latent issues:
--
--   1. `kind` is 'leg' or 'split' — alphabetical sort puts `'leg'`
--      before `'split'`. A security split on the same date as
--      same-day buys/sells is applied AFTER the legs, not before.
--      Wrong: a split adjusts pre-existing holdings; same-day
--      legs should land in post-split units.
--
--   2. Within a date's leg events, sort is by `leg_id` —
--      effectively random. With the quantity clamp gone (mig 116)
--      this no longer affects end-of-walk quantity, but it still
--      affects cost basis under avg-cost: a sell processed before
--      its same-day funding buy/reinvest computes avg-cost against
--      a smaller (or empty) inventory pool, producing a different
--      basis trajectory than the reverse order. Determinism
--      requires picking a rule.
--
-- THE FIX
--
-- Sort within `event_at`:
--   * splits first (apply ratio to existing holdings),
--   * then buys + reinvests (qty > 0; establish lots),
--   * then sells (qty < 0; consume FIFO from established lots),
--   * `leg_id` final tiebreaker for full determinism.
--
-- Rationale: matches the natural causal chain. Lots must exist
-- before they can be consumed; a split must be applied to existing
-- holdings before same-day activity lands. Trades off against
-- the (unavailable) broker-true intra-day order in favour of a
-- predictable, defensible heuristic — quantity is unaffected
-- (mig 116 made quantity order-independent); basis under avg-cost
-- shifts slightly for histories with same-day buy/sell
-- interleaving, in favour of the more defensible "buys-before-
-- sells" computation.
--
-- The mig 116 clamp removal becomes unreachable for the common
-- intra-day-ordering case (sells now fire against established
-- inventory only). It stays in place as defence-in-depth for
-- histories with TRUE over-sells (e.g., the soft-deleted-buy
-- phantom case mig 117 addresses, in which the buy is filtered
-- out by `is_hidden = false` and the surviving sell legitimately
-- has nothing to consume).
--
-- ONE-SHOT REPAIR
--
-- Full-ledger recompute under the new ordering. Quantities will
-- not move; cost-basis values may shift slightly for any holding
-- whose history has same-day buys+sells (or a same-day split with
-- adjacent leg activity).
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

        -- Lot reset. Hidden-header legs (mig 117) are excluded.
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
                NULL::NUMERIC AS ratio,
                -- Mig 118 (ADR-0041): intra-day sort class.
                -- 0 = split (handled by the split branch below; legs
                --     never produce 0 here, kept reserved for clarity)
                -- 1 = buy / reinvest (qty > 0; establishes lots)
                -- 2 = sell           (qty < 0; consumes FIFO)
                CASE
                    WHEN l.quantity > 0 THEN 1
                    ELSE 2
                END AS sort_class
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL
              AND hd.is_hidden = FALSE

            UNION ALL

            SELECT
                'split'::TEXT AS kind,
                ss.split_at AS event_at,
                NULL::UUID AS leg_id,
                NULL::UUID AS header_id,
                NULL::NUMERIC AS amount,
                NULL::NUMERIC AS quantity,
                ss.ratio,
                0 AS sort_class    -- splits first on the same date
            FROM security_splits ss
            WHERE ss.security_id = v_holding.security_id
              AND ss.ledger_id   = v_holding.ledger_id

            -- Mig 118: sort by sort_class within event_at so splits
            -- apply before same-day legs, and buys/reinvests land
            -- before same-day sells. leg_id is the final tiebreaker
            -- for full determinism within a sort_class bucket.
            ORDER BY event_at, sort_class, leg_id
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
    '(ADR-0039). Mig 117 excluded soft-hidden headers (ADR-0040). Mig 118 '
    '(ADR-0041) gives same-date events a deterministic order: splits, '
    'then buys + reinvests, then sells, with leg_id as the final tiebreaker.';

-- One-shot repair under the new ordering.
SELECT recompute_holdings_cost_basis();
