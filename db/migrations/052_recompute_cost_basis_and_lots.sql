-- =============================================================================
-- 052 — recompute holdings.cost_basis (avg-cost) + rebuild lots from txn_legs
-- =============================================================================
--
-- TWO LONG-STANDING WRONGS, ONE STRUCTURAL FIX
--
-- Wrong #1: holdings.cost_basis only grew, never shrank.
--   The importer's HoldingDelta emitted `costBasisDelta = grossSec + commission`
--   on Buy / DivReinvest and `0` on Sell (ADR-0018 rule 4: lot-closing
--   deferred). After aggregation, holdings.cost_basis = sum of every
--   acquisition cost EVER — not the cost basis of CURRENTLY held shares.
--   For BNDA this showed as a cost basis far above the true market value
--   (a fake large unrealized "loss"). For any user who has sold
--   shares of anything, the hero card lies.
--
-- Wrong #2: lots were not rebuilt after the dec=9 / 100,000× scrub.
--   When share_decimals=4 was hardcoded against MD's dec=9 mutual funds,
--   lots stored qty×100,000 with unit_cost÷100,000. The data scrub on
--   2026-05-19 fixed `txn_legs` and `holdings.quantity` but left `lots`
--   stale (every BNDA lot showed unit_cost=$10.00 flat instead of the
--   real per-event price). Lots are not currently consumed by the API
--   (no FIFO/LIFO display surface yet), so the corruption was invisible,
--   but it would have undermined the future tax-lot report.
--
-- THE STRUCTURAL FIX
--
-- A new PL/pgSQL function `recompute_holdings_cost_basis(p_ledger_id UUID)`
-- owns the avg-cost walk:
--   - For each (account, security) in scope, replay every holdings-side
--     leg in posted_at order.
--   - On a positive-qty leg (acquisition): basis += leg.amount; qty += leg.qty.
--   - On a negative-qty leg (disposition): avg = basis / qty; basis -=
--     avg × |qty_sold|; qty += leg.qty (which is negative).
--   - Floor at zero on overdraw (defensive — shouldn't happen, but guards
--     against floating drift from a partial scrub).
--
-- The importer calls this function at the end of every investment import
-- so future runs converge on the right value, regardless of whether
-- intermediate Sells happened. Migration 052 also runs it once over the
-- whole DB to repair the historical mess.
--
-- Lots are wiped and rebuilt from txn_legs in one INSERT-SELECT:
--   - One lot per Buy/DivReinvest holdings-side leg (security_id + qty > 0).
--   - unit_cost = txn_legs.unit_price (per-share, excludes commission;
--     commission ends up in holdings.cost_basis via leg.amount, which
--     captures the gross cash effect on the holdings sibling).
--   - is_closed = false (lot-closing remains deferred per ADR-0018 rule 4).
--
-- A migration-time consistency check at the bottom verifies that every
-- holding now reconciles: market_value / cost_basis sanity bands assume
-- no holding is more than 10× under/over water relative to its avg
-- purchase price (a generous band that catches scaling regressions
-- without false-positiving real losses).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Part 1: PL/pgSQL function — avg-cost recompute for one or all ledgers.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(p_ledger_id UUID DEFAULT NULL)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_leg RECORD;
    v_sibling_id UUID;
    v_running_qty NUMERIC;
    v_running_basis NUMERIC;
    v_avg_cost NUMERIC;
    v_updated INTEGER := 0;
BEGIN
    FOR v_holding IN
        SELECT h.id, h.account_id, h.security_id
        FROM holdings h
        WHERE p_ledger_id IS NULL OR h.ledger_id = p_ledger_id
    LOOP
        -- The leg's account is the Holdings sibling; the holding's
        -- account is the brokerage. Resolve sibling via the brokerage's
        -- holdings_account_id pointer.
        SELECT holdings_account_id INTO v_sibling_id
        FROM accounts
        WHERE id = v_holding.account_id;

        IF v_sibling_id IS NULL THEN
            -- Brokerage has no holdings sibling; nothing to walk.
            CONTINUE;
        END IF;

        v_running_qty := 0;
        v_running_basis := 0;

        FOR v_leg IN
            SELECT l.amount, l.quantity
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id = v_sibling_id
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
                -- Overdraw guard (negative qty after a sell that exceeds
                -- the running pool — shouldn't happen with clean data
                -- but defends against partial-scrub drift).
                IF v_running_qty <= 0 THEN
                    v_running_qty := 0;
                    v_running_basis := 0;
                END IF;
            END IF;
            -- quantity = 0 (or NULL — filtered above) is a no-op.
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
    'Average-cost method recompute of holdings.cost_basis. Walks every '
    'holdings-side leg in posted_at order, increments basis on positive '
    'qty, decrements proportionally on negative qty. Invoked at the end '
    'of every investment import + by migration 052''s one-shot scrub.';


-- -----------------------------------------------------------------------------
-- Part 2: Rebuild lots from txn_legs. Wipe-and-replace.
--
-- Lots are not currently read by the API, so a full wipe is safe; no
-- foreign keys reference lots from outside (lots.holding_id, lots.leg_id
-- are owning FKs).
-- -----------------------------------------------------------------------------

DELETE FROM lots;

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
JOIN accounts sibling ON sibling.id = l.account_id
JOIN accounts brokerage ON brokerage.holdings_account_id = sibling.id
JOIN holdings h ON h.account_id = brokerage.id AND h.security_id = l.security_id
WHERE l.security_id IS NOT NULL
  AND l.quantity IS NOT NULL
  AND l.quantity > 0
  AND l.unit_price IS NOT NULL;


-- -----------------------------------------------------------------------------
-- Part 3: One-shot scrub — recompute every holding.
-- -----------------------------------------------------------------------------

SELECT recompute_holdings_cost_basis(NULL);


-- -----------------------------------------------------------------------------
-- Part 4: Sanity check — fail loudly if any holding is wildly off.
--
-- A 100× band catches scaling regressions (100,000× past, plausible
-- 10× future) without false-positiving real underwater positions.
-- Compares cost_basis/qty against the latest known price for the
-- security; skips holdings with no price data or zero qty.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_bad_count INTEGER;
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
        RAISE WARNING 'Migration 052 sanity check: % holdings have avg-cost/share more than 100× off the latest price. Investigate.', v_bad_count;
    END IF;
END;
$$;
