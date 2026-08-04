-- 155_security_prices_price_scale.sql
-- ADR-0070 D8. security_prices.price was NUMERIC(25,12) while its own high / low
-- columns are NUMERIC(19,4). That 12dp scale let single-precision FLOAT noise be
-- stored — e.g. 7.15 written as 7.150000095367, 10.35 as 10.350009934741 — in
-- historical `fetch` (pre-0.12.0 SimpleFIN) and `import` (MD seed) rows; the true
-- values are clean prices, the tail is representation noise from a producer that
-- went through a 32-bit float. (Current producers already round to 4dp.)
--
-- Constrain price to NUMERIC(19,4), matching high / low: the numeric->numeric
-- cast ROUNDS every existing value to 4dp (scrubbing the noise in one shot), and
-- the DB now ENFORCES 4dp so no writer can reintroduce it (constraints over
-- workarounds). 4dp is ample for valuation — the high/low band already says so.
--
-- Scope: ONLY security_prices.price (a market-valuation snapshot). The TRADE
-- price txn_legs.unit_price stays NUMERIC(25,12) — a per-share execution price
-- (amount / shares) legitimately needs >4dp — and is a different column, not
-- touched here. No view/matview depends on security_prices, so the ALTER is safe.
BEGIN;

ALTER TABLE security_prices
    ALTER COLUMN price TYPE NUMERIC(19,4);

COMMIT;
