-- =============================================================================
-- 116 — recompute_holdings_cost_basis: drop quantity clamp
-- =============================================================================
--
-- ROOT CAUSE
--
-- Prior body (mig 068) had two guards that made the final running
-- quantity depend on intra-event ordering:
--
--   1. The sell branch only fired when `v_running_qty > 0`. A sell
--      encountered with running_qty at exactly 0 was SKIPPED — the
--      negative quantity was lost.
--   2. After the sell branch's subtraction, `running_qty` was clamped
--      to 0 if it would have gone negative; `running_basis` was also
--      zeroed in that case.
--
-- Both were intended to enforce the "you can't own negative shares"
-- invariant. They backfire on legitimate histories whose intra-day
-- order cannot be faithfully recovered from MD (`posted_at` has no
-- intra-day timestamp; the importer lands events in MD's display
-- order, not the broker's true sequence).
--
-- For an internally consistent history (lifetime SUM of quantity
-- equals broker's current holdings), the algorithm SHOULD walk to
-- that SUM regardless of intra-day permutation. The clamp violated
-- that property: any intra-day permutation in which a transient
-- sell drove `running_qty` momentarily negative produced a residual
-- positive quantity at the end, drawn from later buys/reinvests
-- that were added against the clamped-to-zero base.
--
-- Real-data evidence: PTRQX on the user's ledger walked to
-- 0.345 shares despite a lifetime SUM of exactly 0. The 0.345
-- arose entirely from clamp-induced order-dependence on
-- 2026-04-15 (15 mixed events same posted_at).
--
-- THE FIX
--
-- Quantity becomes a pure running sum: every event adds its signed
-- quantity to `running_qty`, no guards, no clamps. End-of-walk
-- equals lifetime SUM. Order is irrelevant.
--
-- Cost basis stays best-effort under avg-cost. The avg-cost
-- reduction on a sell only fires when `running_qty > 0` (positive
-- inventory to consume against). When `running_qty <= 0` at the
-- moment of a sell, the basis is left alone — there's no inventory
-- to value the disposition against, and any imputation would be
-- arbitrary. Clean-data histories (final qty = 0 from matched
-- buy/sell pairs) still produce basis = 0 at end-of-walk.
--
-- Lot consumption is unchanged: the FIFO loop still only consumes
-- open lots until either the sell quantity is exhausted or no open
-- lots remain. Over-sells past the open-lot supply silently
-- don't consume from non-existent lots — same behavior as before.
--
-- ONE-SHOT REPAIR
--
-- Existing data was computed with the clamp. To converge every
-- holding to the new contract, the migration ends with a full-
-- ledger recompute (`recompute_holdings_cost_basis()` with all
-- NULL parameters → walks every holding row).
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

            ELSIF v_event.quantity < 0 THEN
                -- ADR-0039 / mig 116: no `running_qty > 0` guard; sells
                -- always count toward the running sum. Avg-cost reduction
                -- still only applies when there's positive inventory to
                -- value the disposition against.
                IF v_running_qty > 0 THEN
                    v_avg_cost := v_running_basis / v_running_qty;
                    v_running_basis := v_running_basis
                        - (v_avg_cost * LEAST(v_running_qty, ABS(v_event.quantity)));
                END IF;
                v_running_qty := v_running_qty + v_event.quantity;
                -- No clamp at zero. Transient-negative running_qty is
                -- preserved; subsequent buys/reinvests add back to it,
                -- and end-of-walk = lifetime SUM(quantity) regardless of
                -- the intra-event permutation. The basis-zeroing on
                -- exact-zero qty (clean fully-sold case) emerges
                -- naturally from the avg-cost reduction above: at the
                -- moment running_qty hits 0 exactly, avg_cost *
                -- abs(qty_sold) equals the entire remaining basis.

                -- Lot consumption (FIFO). Only fires while open lots
                -- exist; over-sells past the supply are silently not
                -- consumed (no lot to touch).
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
    'holding in chronological order. Migration 116 (ADR-0039) removed the '
    'quantity clamp: sells always count toward running_qty regardless of '
    'whether running_qty was already 0; no clamping when sells drive '
    'running_qty negative. End-of-walk quantity equals lifetime SUM(quantity) '
    'and is invariant under intra-day permutation. Avg-cost basis reduction '
    'still gates on positive inventory; over-sells past available lots '
    'silently don''t consume from non-existent lots.';

-- -----------------------------------------------------------------------------
-- One-shot repair: re-walk every holding under the new contract so the
-- stored quantity + cost_basis values converge.
-- -----------------------------------------------------------------------------

SELECT recompute_holdings_cost_basis();
