-- =============================================================================
-- 054 — B0.1 (commission in basis) + B0.2 (FIFO lot closing on Sells)
-- =============================================================================
--
-- Two precision fixes that ride together because they walk the same event
-- stream — the recompute function gains two responsibilities in one pass:
--   1. Average-cost basis on holdings, with commission folded in.
--   2. FIFO lot closure on Sells.
--
-- B0.1 — Commission inclusion
-- ---------------------------
-- ADR-0018 Rule 3 originally specified `lots.unit_cost` would carry
-- apportioned commission ("Cost basis includes commission per IRS"). The
-- intent stalled when migration 046 dropped the per-leg `commission`
-- column in favor of the ADR-0019 fee-leg pattern, and migration 053's
-- recompute summed only `holdings_leg.amount` (= qty × unit_price, no
-- commission). For free mutual-fund trades that's exact; for any Buy
-- through a paid brokerage it understates basis.
--
-- The hard part is distinguishing real broker commissions from
-- in-transaction account-fee shortcuts. Probe against real data showed:
--   - 95+399 Sell-side "Fees" / "Investment Fees" rows ($6.4K) — 401k
--     administrative fees deducted by forced sale. Not commissions.
--   - a handful of Buy-side option-trading commissions over several years
--     (a small total) — legitimate broker commissions.
--   - a few rows with fee = -gross_buy — reconciliation dirt.
-- The data is ambiguous; only the user knows which category is which.
--
-- Solution: per-category opt-in. `accounts.is_trade_commission` defaults
-- to FALSE so behavior is unchanged from migration 053 until the user
-- explicitly flips it on a real commission category. The recompute
-- function then adds that category's leg amounts to basis for events
-- where the fee leg's same-posting cash counterpart is on an
-- investment-typed account (Option B structural gate — flipping the
-- flag on a bank-fee category by mistake can't pollute investment
-- basis).
--
-- B0.2 — FIFO lot closing
-- -----------------------
-- ADR-0018 Rule 4 deferred lot-closing on Sells. Every lot currently
-- carries `is_closed = FALSE` regardless of how many shares have been
-- disposed. That's fine for the hero's avg-cost basis (computed off
-- holdings, not lots) but blocks any per-lot tax surface — realized
-- gains, short-vs-long-term breakdown, A5's Edit Lots.
--
-- Closing rule: walk Sells in posted_at order; for each Sell, drain
-- open lots by acquired_at ASC (FIFO default). Partial closes
-- decrement `lots.quantity`; full closes set `is_closed = TRUE` and
-- `quantity = 0`. Acquired quantity is preserved via the lot's
-- `leg_id` pointer to the source txn_leg (which is immutable).
--
-- Idempotency
-- -----------
-- Critical because the user will flip the commission flag and re-run.
-- The function resets every affected lot at the start of each
-- holding's loop (`quantity` ← source leg's quantity, `is_closed` ←
-- FALSE) so re-running produces the same result regardless of prior
-- lot state. This also means the function is safe to call from the
-- importer pipeline on every import.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Part 1: Schema — per-category commission flag
-- -----------------------------------------------------------------------------

ALTER TABLE accounts
    ADD COLUMN is_trade_commission BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN accounts.is_trade_commission IS
    'When TRUE on an expense category, recompute_holdings_cost_basis() '
    'adds this category''s same-header leg amounts to cost basis for '
    'investment events. Gated by Option B: the fee leg''s same-posting '
    'cash counterpart must be on an account_type=''investment'' row, so '
    'flipping the flag on a non-investment fee category can''t pollute '
    'investment basis. Default FALSE; set explicitly via the Categories '
    'management UI (or one-shot UPDATE) once real broker commissions are '
    'distinguished from in-transaction account-fee shortcuts.';


-- -----------------------------------------------------------------------------
-- Part 2: Updated function — avg-cost basis + FIFO lot closure
--
-- Replaces the 053 version. Same external signature so the importer's
-- Pass 5 call site is unchanged.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(p_ledger_id UUID DEFAULT NULL)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_leg RECORD;
    v_lot RECORD;
    v_running_qty NUMERIC;
    v_running_basis NUMERIC;
    v_avg_cost NUMERIC;
    v_fee NUMERIC;
    v_remaining_sell NUMERIC;
    v_updated INTEGER := 0;
BEGIN
    FOR v_holding IN
        SELECT id, account_id, security_id
        FROM holdings
        WHERE p_ledger_id IS NULL OR ledger_id = p_ledger_id
    LOOP
        -- Idempotency reset: restore every lot for this holding to its
        -- acquired state (quantity = source leg's quantity, not closed).
        -- The function is then deterministic regardless of prior lot
        -- state — flip a flag, re-run, lots converge.
        UPDATE lots l
        SET quantity  = tl.quantity,
            is_closed = FALSE
        FROM txn_legs tl
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id;

        v_running_qty   := 0;
        v_running_basis := 0;

        FOR v_leg IN
            SELECT l.amount, l.quantity, l.header_id
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL
            ORDER BY hd.posted_at, l.id
        LOOP
            IF v_leg.quantity > 0 THEN
                -- Acquisition: basis += leg.amount + commission.
                -- Commission inclusion gated on:
                --   (a) per-category flag is_trade_commission = TRUE
                --   (b) the fee leg's same-posting cash counterpart is
                --       on an investment-typed account (Option B
                --       structural gate — bank-account fees mis-flagged
                --       can't leak in).
                v_fee := COALESCE((
                    SELECT SUM(fl.amount)
                    FROM txn_legs fl
                    JOIN accounts fa ON fa.id = fl.account_id
                    WHERE fl.header_id = v_leg.header_id
                      AND fa.account_type = 'category'
                      AND fa.is_trade_commission = TRUE
                      AND EXISTS (
                          SELECT 1
                          FROM txn_legs sl
                          JOIN accounts sa ON sa.id = sl.account_id
                          WHERE sl.header_id     = fl.header_id
                            AND sl.posting_index = fl.posting_index
                            AND sl.id           <> fl.id
                            AND sa.account_type  = 'investment'
                      )
                ), 0);
                v_running_qty   := v_running_qty + v_leg.quantity;
                v_running_basis := v_running_basis + v_leg.amount + v_fee;

            ELSIF v_leg.quantity < 0 AND v_running_qty > 0 THEN
                -- Disposition: avg-cost basis reduction.
                v_avg_cost      := v_running_basis / v_running_qty;
                v_running_basis := v_running_basis - (v_avg_cost * ABS(v_leg.quantity));
                v_running_qty   := v_running_qty + v_leg.quantity;
                IF v_running_qty <= 0 THEN
                    v_running_qty   := 0;
                    v_running_basis := 0;
                END IF;

                -- FIFO lot closure. Drain |sell_qty| across open lots
                -- in acquired_at ASC order; partial closes decrement
                -- lots.quantity; full closes flip is_closed.
                v_remaining_sell := ABS(v_leg.quantity);
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
                -- If v_remaining_sell > 0 after the loop, the user
                -- sold more shares than they ever acquired (overdraw).
                -- Same as the basis floor above — defensive no-op
                -- rather than an exception, since real data may have
                -- shape drift from partial scrubs.
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

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID) IS
    'Combined avg-cost basis + FIFO lot closure (migration 054). For each '
    'holding: walks holdings-side legs in posted_at order, increments '
    'basis on acquisitions (with commission from flagged categories via '
    'Option B gate), reduces basis on dispositions (avg-cost method), and '
    'closes open lots in acquired_at order on each Sell. Resets lot state '
    'at the start of each holding so re-runs are idempotent. Called from '
    'the importer pipeline as Pass 5 and from migration 054 as the '
    'one-shot scrub.';


-- -----------------------------------------------------------------------------
-- Part 3: Lots rebuild — commission-aware unit_cost
--
-- Wipe and rebuild so the unit_cost reflects post-054 commission rules.
-- For flag=FALSE categories (default) the result is identical to 053.
-- -----------------------------------------------------------------------------

DELETE FROM lots;

INSERT INTO lots (id, ledger_id, holding_id, leg_id, quantity, unit_cost, acquired_at, is_closed)
SELECT
    gen_random_uuid(),
    l.ledger_id,
    h.id,
    l.id,
    l.quantity,
    (l.amount + COALESCE((
        SELECT SUM(fl.amount)
        FROM txn_legs fl
        JOIN accounts fa ON fa.id = fl.account_id
        WHERE fl.header_id = l.header_id
          AND fa.account_type = 'category'
          AND fa.is_trade_commission = TRUE
          AND EXISTS (
              SELECT 1
              FROM txn_legs sl
              JOIN accounts sa ON sa.id = sl.account_id
              WHERE sl.header_id     = fl.header_id
                AND sl.posting_index = fl.posting_index
                AND sl.id           <> fl.id
                AND sa.account_type  = 'investment'
          )
    ), 0)) / l.quantity AS unit_cost,
    hd.posted_at,
    FALSE
FROM txn_legs l
JOIN txn_headers hd ON hd.id = l.header_id
JOIN holdings h ON h.account_id = l.account_id AND h.security_id = l.security_id
WHERE l.security_id IS NOT NULL
  AND l.quantity IS NOT NULL
  AND l.quantity > 0
  AND l.unit_price IS NOT NULL;


-- -----------------------------------------------------------------------------
-- Part 4: One-shot scrub — recompute every holding's basis + close lots.
-- -----------------------------------------------------------------------------

SELECT recompute_holdings_cost_basis(NULL);


-- -----------------------------------------------------------------------------
-- Part 5: Sanity bands — same 100× band as 053; warns if a holding ends
-- up wildly off latest market price.
-- -----------------------------------------------------------------------------

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
        ORDER BY price_date DESC LIMIT 1
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

        RAISE NOTICE 'Migration 054 sanity: % holdings >100x off latest price. Sample: ticker=% cost_basis=% qty=% avg_cost=%',
            v_bad_count, v_sample.ticker, v_sample.cost_basis, v_sample.quantity, v_sample.avg_cost;
    END IF;
END;
$$;
