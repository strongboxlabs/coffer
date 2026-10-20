-- =============================================================================
-- 148 — FIFO cost basis + realized_gains (ADR-0064)
-- =============================================================================
--
-- Coffer displayed holding cost basis as AVERAGE COST (the recompute function's
-- running v_running_basis / v_running_qty) while ALSO consuming the `lots` table
-- FIFO. The two diverge after any partial sale. ADR-0064 makes the whole app
-- FIFO: the holding's cost_basis is the sum of its open FIFO lots' cost, and each
-- sale records a realized gain.
--
-- WHAT CHANGES IN recompute_holdings_cost_basis (vs mig 118):
--   * Sell branch: instead of reducing basis by avg_cost × qty, reduce it by the
--     COST OF THE FIFO LOTS CONSUMED (accumulated in the existing lot-consume
--     loop). This keeps v_running_basis == Σ(open lot qty × unit_cost) at all
--     times — true FIFO basis. With no sells, FIFO ≡ average cost (unchanged).
--   * Split branch: also divide each open lot's unit_cost by the ratio (quantity
--     is multiplied by it), so lot COST stays constant across a split. Under the
--     old avg-cost basis this didn't matter (basis tracked separately); under
--     FIFO (basis = Σ qty × unit_cost) it must, or a split would inflate basis.
--   * Each sell records one realized_gains row: proceeds (the security-leg market
--     amount; net of a sell-side fee when the brokerage folds fees, mirroring
--     buys), cost basis consumed, realized gain = proceeds − cost consumed.
--
-- One-shot full recompute backfills every holding's FIFO basis + realized_gains.
-- =============================================================================

-- ---- realized_gains -------------------------------------------------------
-- One row per sell leg (the holdings-side disposal leg). Owned by the recompute
-- function: it deletes + repopulates the rows in its (account, security) scope
-- each run, so the table is always consistent with the lots. account_id is the
-- holdings-sibling account (resolve to the owning brokerage via
-- accounts.holdings_account_id, as the Portfolio View does).
CREATE TABLE realized_gains (
    id               UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    ledger_id        UUID        NOT NULL REFERENCES ledgers(id) ON DELETE CASCADE,
    account_id       UUID        NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    security_id      UUID        NOT NULL REFERENCES securities(id) ON DELETE CASCADE,
    sell_leg_id      UUID        NOT NULL REFERENCES txn_legs(id) ON DELETE CASCADE,
    sold_at          TIMESTAMPTZ NOT NULL,
    quantity         NUMERIC     NOT NULL,
    proceeds         NUMERIC     NOT NULL,
    cost_basis_sold  NUMERIC     NOT NULL,
    realized_gain    NUMERIC     NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT uq_realized_gains_sell_leg UNIQUE (sell_leg_id)
);

CREATE INDEX idx_realized_gains_security ON realized_gains (security_id);
CREATE INDEX idx_realized_gains_account  ON realized_gains (account_id);
CREATE INDEX idx_realized_gains_sold_at  ON realized_gains (sold_at);

COMMENT ON TABLE realized_gains IS
    'ADR-0064: per-sale realized gains (FIFO). Owned by '
    'recompute_holdings_cost_basis (delete + repopulate per (account, security) '
    'scope). proceeds net of sell-side fee when the brokerage folds fees; '
    'realized_gain = proceeds - cost_basis_sold (FIFO lots consumed).';

ALTER TABLE realized_gains ENABLE ROW LEVEL SECURITY;
ALTER TABLE realized_gains FORCE  ROW LEVEL SECURITY;

-- Per-user via the securities sub-select (transitively ledger-scoped), the same
-- pattern security_splits uses (mig 060).
CREATE POLICY realized_gains_per_user ON realized_gains
    FOR ALL
    TO coffer_app
    USING (security_id IN (SELECT id FROM securities))
    WITH CHECK (security_id IN (SELECT id FROM securities));

GRANT SELECT, INSERT, UPDATE, DELETE ON realized_gains TO coffer_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON realized_gains TO coffer_service;

-- ---- recompute_holdings_cost_basis: FIFO basis + realized gains ------------
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

        -- Lot reset to acquired state. Hidden-header legs (mig 117) excluded.
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
                CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
            FROM txn_legs l
            JOIN live_txn_headers hd ON hd.id = l.header_id   -- excludes recurring templates (mig 124)
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
                -- Buy / reinvest: add the lot's cost (with fee when the brokerage
                -- folds fees) to basis. v_running_basis stays = Σ open-lot cost.
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
                -- Sell: consume FIFO lots, accumulating the cost consumed; reduce
                -- basis by that cost (NOT avg-cost), and record the realized gain.
                v_remaining_sell := ABS(v_event.quantity);
                v_consumed_cost  := 0;
                FOR v_lot IN
                    SELECT id, quantity, unit_cost
                    FROM lots
                    WHERE holding_id = v_holding.id
                      AND is_closed  = FALSE
                      AND quantity   > 0
                    ORDER BY acquired_at, id
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
                    v_remaining_sell := v_remaining_sell - v_consumed_from_lot;
                END LOOP;

                v_running_basis := v_running_basis - v_consumed_cost;
                v_running_qty   := v_running_qty + v_event.quantity;

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

                INSERT INTO realized_gains (
                    ledger_id, account_id, security_id, sell_leg_id,
                    sold_at, quantity, proceeds, cost_basis_sold, realized_gain)
                VALUES (
                    v_holding.ledger_id, v_holding.account_id, v_holding.security_id,
                    v_event.leg_id, v_event.event_at, ABS(v_event.quantity),
                    v_proceeds, v_consumed_cost, v_proceeds - v_consumed_cost);
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
    'ADR-0064: FIFO cost basis. Walks the (txn_legs union security_splits) event '
    'stream per holding (mig 118 ordering: splits, buys/reinvests, sells). Holding '
    'cost_basis = Σ open-lot cost; sells consume lots FIFO, reduce basis by the '
    'consumed cost, and record a realized_gains row. Splits scale lot quantity up '
    'and unit_cost down so lot cost is unchanged.';

-- One-shot full recompute: every holding to FIFO basis + realized_gains backfill.
SELECT recompute_holdings_cost_basis();
