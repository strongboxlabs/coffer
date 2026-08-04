-- =============================================================================
-- 131 — securities.quote_symbol + auto_price (ADR-0054 D2 coverage override)
-- quote_symbol: the symbol sent to the market-data provider, overriding the
--   display ticker for securities whose provider symbol differs (mutual funds,
--   international suffixes). NULL → fall back to ticker.
-- auto_price: whether the security participates in automated price fetches.
--   Default true (existing + imported securities auto-price, matching prior
--   behavior); set false to pin a price by hand (e.g. a stable-NAV
--   money-market fund) without nulling its ticker.
-- =============================================================================

BEGIN;

ALTER TABLE securities
    ADD COLUMN quote_symbol TEXT    NULL,
    ADD COLUMN auto_price   BOOLEAN NOT NULL DEFAULT true;

COMMIT;
