-- ---------------------------------------------------------------------------
-- 237 — finish what 235 started: a share movement NEVER prices a day.
--
-- ADR-0084 D6 states the principle plainly — a ShrsIn/ShrsOut has no cash
-- consideration, so it has no price. Migration 235 implements only half of it,
-- excluding such a leg when its amount still equals the share count and
-- treating a leg carrying any other figure as a real observation. Both halves
-- of that rule fail:
--
-- 1. THE NOMINAL TEST IS OFF BY A CENT. The nominal amount can be truncated
--    where 235 assumed rounding: a leg whose share count has a third decimal of five carries an
--    amount truncated to two decimals, while round(quantity, 2) rounds that
--    five up. The two differ by a cent, the equality never matches, and the
--    placeholder prices its day a hair under one anyway. Widening to a
--    tolerance would patch that one arithmetic, and leave (2) untouched.
--
-- 2. A "REAL" FIGURE ON A SHARE MOVEMENT IS COST BASIS, NOT A PRICE. An in-kind
--    move carries each lot across at the price it was originally bought at, so
--    one security on one day arrives with as many different unit_prices as there
--    were lots — and they sort AFTER the day's genuine trades by seq, so a lot's
--    historical purchase price displaces the real one. The check then reports
--    disagreements where both the stored and the expected figure are lot basis
--    from an in-kind conversion, and neither is a market price.
--
-- There is no test that separates a basis figure from a price, because nothing
-- in the row distinguishes them. The action does: a share movement is not a
-- trade, whatever number rides along with it. So the action is the whole rule
-- now, and fn_leg_prices_a_day loses its arithmetic.
--
-- WHAT THIS COSTS. A day a genuine trade prices keeps its price. A day whose
-- only pricing leg was a share movement loses its trade price — and on every one
-- of those, no trade occurred; only shares moved. Valuation
-- falls back to the nearest real price or reports the position stale, which is
-- the honest answer where no trade set a price.
--
-- KNOWN TRADE-OFF. Correcting a ShrsOut by hand no longer seeds a price either.
-- Prices already written that way survive untouched (nothing retracts them, and
-- the cleanup below cannot reach a plausible figure), but a FUTURE correction
-- records the transaction without recording a price. That is the right default
-- — the corrected figure is a valuation, not an execution — and the price CRUD
-- exists for saying so deliberately.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_leg_prices_a_day(
    p_quantity numeric,
    p_unit_price numeric,
    p_amount numeric,
    p_provider_action text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT p_quantity   IS NOT NULL AND p_quantity   <> 0
       AND p_unit_price IS NOT NULL AND p_unit_price > 0
       -- A share movement is not a trade. Whatever unit_price rides along with
       -- it is either the importer's nominal or a lot's carried-over basis;
       -- neither is what the security was worth that day.
       AND coalesce(p_provider_action, '') NOT IN ('ShrsIn', 'ShrsOut');
$$;

COMMENT ON FUNCTION fn_leg_prices_a_day(numeric, numeric, numeric, text) IS
    'Whether a security leg is a genuine price observation. A ShrsIn/ShrsOut is '
    'never one: it has no cash consideration, so its unit_price is the importer''s '
    'nominal or a lot''s carried basis. The amount arguments are retained for '
    'callers and for a future provider that needs a different test.';

-- ---------------------------------------------------------------------------
-- Cleanup, on the same narrow terms as migration 236: a trade-source price the
-- widened rule can no longer account for, at a nominal figure, on a security
-- that demonstrably trades far above it.
--
-- Deliberately NOT "delete every row the new rule orphans". A plausible figure
-- — a price a little under what the fund was then worth — may well be a real past
-- observation, and ADR-0084 D4's reasoning applies to it: deleting that is
-- discarding evidence, not cleaning up. Only the figures that cannot be true
-- go.
-- ---------------------------------------------------------------------------

DELETE FROM security_prices p
WHERE p.source = 'trade'
  AND p.price BETWEEN 0.9 AND 1.1
  AND fn_trade_price_for_day(p.ledger_id, p.security_id, p.price_date) IS NULL
  AND EXISTS (
        SELECT 1 FROM security_prices q
        WHERE q.security_id = p.security_id
          AND q.price > 5);
