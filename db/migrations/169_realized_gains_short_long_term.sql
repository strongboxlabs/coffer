-- =============================================================================
-- 169 — realized_gains short-term / long-term split (ADR-0064 D2 follow-on)
-- =============================================================================
--
-- ADR-0064 D2 earmarked realized_gains as "the home for ... a future short-/
-- long-term breakdown." The FIFO recompute already visits, per sell, each lot it
-- consumes (with the lot's acquired_at, preserved across splits — so the holding
-- period runs from the original purchase, tax-correct), but collapses them into
-- one realized_gains row with only the aggregate cost_basis_sold. This adds the
-- long-term portion to that row so short-vs-long-term gains are queryable.
--
-- WHAT CHANGES:
--   * realized_gains gains proceeds_lt / cost_basis_sold_lt / realized_gain_lt —
--     the LONG-TERM portion of each sale. The short-term portion is the total
--     minus the LT portion (derived in the reporting layer), so there's no
--     redundant column pair.
--   * recompute_holdings_cost_basis's sell branch buckets each consumed lot by
--     holding period: long-term iff sold_at > acquired_at + 1 year (US rule:
--     "more than one year"; exactly one year is short-term). cost_basis_sold_lt
--     accumulates the LT lots' cost; proceeds are apportioned to LT by consumed-
--     share share. A sale straddling the 1-year line splits across both buckets.
--   * transfer_shares disposals still record NO realized_gains row (ADR-0065 D1).
--
-- Body is otherwise verbatim from migration 163 (merged-aware FIFO recompute);
-- only the sell branch's per-lot bucketing + the INSERT column list change.
-- One-shot full recompute at the end backfills the LT split for all history.
-- =============================================================================

ALTER TABLE realized_gains
    ADD COLUMN proceeds_lt        NUMERIC NOT NULL DEFAULT 0,
    ADD COLUMN cost_basis_sold_lt NUMERIC NOT NULL DEFAULT 0,
    ADD COLUMN realized_gain_lt   NUMERIC NOT NULL DEFAULT 0;

COMMENT ON COLUMN realized_gains.proceeds_lt IS
    'ADR-0064: long-term portion of proceeds (lots held > 1 year at sale). '
    'Short-term proceeds = proceeds - proceeds_lt.';
COMMENT ON COLUMN realized_gains.cost_basis_sold_lt IS
    'ADR-0064: long-term portion of cost_basis_sold. Short-term = cost_basis_sold '
    '- cost_basis_sold_lt.';
COMMENT ON COLUMN realized_gains.realized_gain_lt IS
    'ADR-0064: long-term realized gain (proceeds_lt - cost_basis_sold_lt). '
    'Short-term gain = realized_gain - realized_gain_lt.';

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
    v_fee NUMERIC;
    v_remaining_sell NUMERIC;
    v_consumed_cost NUMERIC;
    v_consumed_from_lot NUMERIC;
    v_proceeds NUMERIC;
    v_updated INTEGER := 0;
    v_resolved_ledger_id UUID;
    -- ADR-0064 ST/LT split (mig 169): long-term buckets accumulated per sell.
    v_consumed_cost_lt NUMERIC;
    v_qty_lt NUMERIC;
    v_consumed_qty NUMERIC;
    v_proceeds_lt NUMERIC;
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

        -- Realized gains are recomputed from scratch for this (account, security).
        DELETE FROM realized_gains
        WHERE account_id  = v_holding.account_id
          AND security_id = v_holding.security_id;

        -- Lot reset to acquired state. Hidden/merged-header legs excluded.
        -- transfer_shares destination lots are leg-derived 1:1 (one lot per
        -- destination leg, leg amount = lot cost), so this generic reset
        -- re-derives their inherited unit_cost correctly — no special case.
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
        JOIN live_txn_headers th ON th.id = tl.header_id   -- excludes recurring templates (mig 124)
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id
          AND th.is_hidden = FALSE
          AND th.is_merged_into IS NULL;

        v_running_qty   := 0;
        v_running_basis := 0;

        FOR v_event IN
            SELECT
                'leg'::TEXT AS kind,
                hd.posted_at AS event_at,
                hd.action AS action,
                l.id AS leg_id,
                l.header_id,
                l.amount,
                l.quantity,
                NULL::NUMERIC AS ratio,
                CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
            FROM txn_legs l
            JOIN live_txn_headers hd ON hd.id = l.header_id   -- excludes recurring templates (mig 124)
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL
              AND hd.is_hidden = FALSE
              AND hd.is_merged_into IS NULL

            UNION ALL

            SELECT
                'split'::TEXT AS kind,
                ss.split_at AS event_at,
                NULL::TEXT AS action,
                NULL::UUID AS leg_id,
                NULL::UUID AS header_id,
                NULL::NUMERIC AS amount,
                NULL::NUMERIC AS quantity,
                ss.ratio,
                0 AS sort_class
            FROM security_splits ss
            WHERE ss.security_id = v_holding.security_id
              AND ss.ledger_id   = v_holding.ledger_id

            ORDER BY event_at, sort_class, leg_id
        LOOP
            IF v_event.kind = 'split' THEN
                v_running_qty := v_running_qty * v_event.ratio;

                -- Quantity scales by the ratio; unit_cost scales inversely so the
                -- lot's COST (qty × unit_cost) is unchanged — required under FIFO
                -- basis (ADR-0064), where total basis = Σ open-lot cost.
                UPDATE lots
                SET quantity  = quantity * v_event.ratio,
                    unit_cost = CASE WHEN v_event.ratio <> 0
                                     THEN unit_cost / v_event.ratio
                                     ELSE unit_cost END
                WHERE holding_id = v_holding.id
                  AND is_closed  = FALSE;

            ELSIF v_event.quantity > 0 THEN
                -- Buy / reinvest / transfer-in: add the lot's cost (with fee when
                -- the brokerage folds fees) to basis. v_running_basis stays =
                -- Σ open-lot cost. transfer_shares carries no fee legs, so the
                -- fee term is naturally 0 for a transfer-in event.
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
                -- Sell / transfer-out: consume FIFO lots, accumulating the cost
                -- consumed; reduce basis by that cost (NOT avg-cost). A real sale
                -- records a realized_gains row; a transfer_shares disposal does
                -- NOT (ADR-0065 D1 — it's a transfer, not a sale).
                --
                -- Availability gate (ADR-0065 D3): only consume lots that have
                -- ARRIVED by this event's time — i.e. whose creating leg's header
                -- posted_at <= the disposal time.
                --
                -- ST/LT (mig 169): each consumed lot is long-term iff the sale is
                -- more than one year after the lot's acquired_at (splits preserve
                -- acquired_at, so the clock runs from the original purchase).
                v_remaining_sell   := ABS(v_event.quantity);
                v_consumed_cost    := 0;
                v_consumed_cost_lt := 0;
                v_qty_lt           := 0;
                FOR v_lot IN
                    SELECT l.id, l.quantity, l.unit_cost, l.acquired_at
                    FROM lots l
                    JOIN txn_legs ltl        ON ltl.id = l.leg_id
                    JOIN live_txn_headers lhd ON lhd.id = ltl.header_id
                    WHERE l.holding_id = v_holding.id
                      AND l.is_closed  = FALSE
                      AND l.quantity   > 0
                      AND lhd.posted_at <= v_event.event_at
                    ORDER BY l.acquired_at, l.id
                LOOP
                    EXIT WHEN v_remaining_sell <= 0;

                    IF v_lot.quantity <= v_remaining_sell THEN
                        v_consumed_from_lot := v_lot.quantity;
                        UPDATE lots
                        SET quantity  = 0,
                            is_closed = TRUE
                        WHERE id = v_lot.id;
                    ELSE
                        v_consumed_from_lot := v_remaining_sell;
                        UPDATE lots
                        SET quantity = quantity - v_remaining_sell
                        WHERE id = v_lot.id;
                    END IF;
                    v_consumed_cost  := v_consumed_cost
                                        + v_consumed_from_lot * COALESCE(v_lot.unit_cost, 0);

                    -- Long-term portion: > 1 year from acquisition to sale.
                    IF v_event.event_at > v_lot.acquired_at + INTERVAL '1 year' THEN
                        v_consumed_cost_lt := v_consumed_cost_lt
                                              + v_consumed_from_lot * COALESCE(v_lot.unit_cost, 0);
                        v_qty_lt := v_qty_lt + v_consumed_from_lot;
                    END IF;

                    v_remaining_sell := v_remaining_sell - v_consumed_from_lot;
                END LOOP;

                v_running_basis := v_running_basis - v_consumed_cost;
                v_running_qty   := v_running_qty + v_event.quantity;

                IF v_event.action IS DISTINCT FROM 'transfer_shares' THEN
                    -- Proceeds = -amount on the (negative) holdings-side sell leg,
                    -- net of a sell-side fee when the brokerage folds fees (mirrors
                    -- how buys fold fees into basis; ADR-0064 D2/D4).
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
                    v_proceeds := (-v_event.amount) - v_fee;

                    -- Apportion proceeds to the long-term bucket by consumed-share
                    -- share (cost_basis_sold_lt was accumulated per-lot above).
                    v_consumed_qty := ABS(v_event.quantity) - v_remaining_sell;
                    IF v_consumed_qty > 0 THEN
                        -- Multiply before dividing so an exactly-divisible split
                        -- stays exact (4500 * 10 / 15 = 3000, not 4500 * 0.666… rounded).
                        v_proceeds_lt := v_proceeds * v_qty_lt / v_consumed_qty;
                    ELSE
                        v_proceeds_lt := 0;
                    END IF;

                    INSERT INTO realized_gains (
                        ledger_id, account_id, security_id, sell_leg_id,
                        sold_at, quantity, proceeds, cost_basis_sold, realized_gain,
                        proceeds_lt, cost_basis_sold_lt, realized_gain_lt)
                    VALUES (
                        v_holding.ledger_id, v_holding.account_id, v_holding.security_id,
                        v_event.leg_id, v_event.event_at, ABS(v_event.quantity),
                        v_proceeds, v_consumed_cost, v_proceeds - v_consumed_cost,
                        v_proceeds_lt, v_consumed_cost_lt, v_proceeds_lt - v_consumed_cost_lt);
                END IF;
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
    'ADR-0064/0065: FIFO cost basis. Walks the (txn_legs union security_splits) '
    'event stream per holding. Holding cost_basis = Σ open-lot cost; disposals '
    'consume lots FIFO (only lots ARRIVED by the disposal time) and reduce basis '
    'by the consumed cost. A real sale records a realized_gains row (mig 169: with '
    'the long-term portion split out by per-lot holding period, > 1 year = LT); a '
    'transfer_shares disposal does not. Splits scale lot quantity up and unit_cost '
    'down so lot cost is unchanged. Legs of hidden OR merged-away headers excluded.';

-- One-shot full recompute: backfills the ST/LT split for all realized-gain history.
SELECT recompute_holdings_cost_basis();
