-- =============================================================================
-- 051 — enforce non-negative per-share prices
-- =============================================================================
--
-- Unit price is a magnitude: it's "how many dollars one share is worth".
-- The direction of a trade lives in the qty + amount signs, never in the
-- price. There is no shape where a negative per-share price is meaningful.
--
-- HISTORICAL: InvestmentTransactionMapper.ComputeUnitPrice divided positive
-- cash by signed qty (MD reports SplitAmount as negative for Sells), so
-- every Sell wrote a negative unit_price into txn_legs. A batch of rows
-- across many securities carried this on 2026-05-19 when the scrub +
-- importer fix landed. Symptom in the SPA: Sell rows rendered an
-- illustrative "negative sh × negative price = negative amount" — qty ×
-- price came out positive while amount was negative.
--
-- security_prices.price and lots.unit_cost are guarded too — same
-- invariant, both currently clean but the constraint protects future
-- write paths (manual price entry, lot-recompute jobs).
-- =============================================================================

ALTER TABLE txn_legs
    ADD CONSTRAINT txn_legs_unit_price_nonneg
    CHECK (unit_price IS NULL OR unit_price >= 0);

ALTER TABLE security_prices
    ADD CONSTRAINT security_prices_price_nonneg
    CHECK (price >= 0);

ALTER TABLE lots
    ADD CONSTRAINT lots_unit_cost_nonneg
    CHECK (unit_cost IS NULL OR unit_cost >= 0);

COMMENT ON CONSTRAINT txn_legs_unit_price_nonneg ON txn_legs IS
    'Per-share price is a magnitude; trade direction lives in qty + amount '
    'signs. Migration 051 added after the importer wrote negative unit_price '
    'on every Sell row.';
