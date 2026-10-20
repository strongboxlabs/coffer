-- =============================================================================
-- 177 — trade-derived security prices (ADR-0084)
-- =============================================================================
--
-- A security transaction records an execution price (txn_legs.unit_price =
-- |cash| / |shares|), but no code path wrote that price into security_prices.
-- A held-but-unfed security (a dormant 401(k), a ticker the quote provider
-- doesn't cover) then had price history only from the one-time import seed, so
-- the mig-172 as-of feeder fell back to the last trade price (its tier-2) or,
-- absent even that, valued the holding at 0 — understating net-worth history
-- and returns.
--
-- ADR-0084 makes the execution price seed security_prices at a new `trade`
-- source, ranked BELOW a Yahoo EOD close / manual gap-fill but ABOVE the
-- SimpleFIN intraday balance and the import seed:
--     manual == fetch (Yahoo)  >  trade  >  simplefin  >  import
-- Going forward the TradePriceFromLegInterceptor upserts a `trade` row per
-- (security, UTC-day) via security_price_upsert_from_trade after every EF
-- write that lands an investment trade leg (native API + MCP). The Moneydance
-- importer (Dapper, bypasses EF) runs the identical seed per-ledger at
-- end-of-import via TradePriceSeedStep, so a FUTURE import is covered too; the
-- one-time backfill below covers ledgers that already existed at deploy.
--
-- price_date is the UTC calendar day of posted_at (ADR-0070 D5 / ADR-0084 D3),
-- so a trade and a same-day Yahoo close share one day-row and the rank gate
-- lets the feed overwrite the trade.
-- =============================================================================

BEGIN;

-- 1. Add the `trade` source to the CHECK ladder (ADR-0084 D1). Drop + re-add is
--    the same style mig 154 used to introduce `simplefin`.
ALTER TABLE security_prices
    DROP CONSTRAINT ck_security_prices_source;
ALTER TABLE security_prices
    ADD CONSTRAINT ck_security_prices_source
        CHECK (source IN ('import', 'fetch', 'manual', 'simplefin', 'trade'));

-- 2. The rank-gated upsert primitive (ADR-0084 D2). Invoked post-save by the
--    TradePriceFromLegInterceptor via HasDbFunction — a Postgres function (not
--    a trigger, ADR-0032) so the conflict SQL stays out of the app layer and a
--    tracked SaveChanges inside SavedChanges can't re-fire the interceptors.
--    The UNIQUE (security_id, price_date) target is ADR-0070 D1 (mig 154).
--    The DO UPDATE ... WHERE gate overwrites only import/simplefin/trade rows,
--    so a truer fetch/manual close for the day is never clobbered.
CREATE FUNCTION security_price_upsert_from_trade(
    p_ledger_id   UUID,
    p_security_id UUID,
    p_day         DATE,
    p_price       NUMERIC
)
-- The OUT column is `upserted_security_id`, NOT `security_id`: a RETURNS TABLE
-- column named `security_id` becomes a plpgsql variable that collides with the
-- `security_prices.security_id` column in `ON CONFLICT (security_id, ...)`
-- ("column reference is ambiguous"). Distinct name = no collision.
RETURNS TABLE(upserted_security_id UUID)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    -- A priceless leg (dividend_cash / divx / inc / exp / misc / transfer /
    -- transfer_shares carries pamt = 0 -> unit_price 0/NULL) writes no price.
    -- Echo the input security_id so EF always has a typed projection row.
    IF p_price IS NULL OR p_price <= 0 THEN
        RETURN QUERY SELECT p_security_id;
        RETURN;
    END IF;

    INSERT INTO security_prices (id, security_id, ledger_id, price, currency_code, price_date, source)
    VALUES (gen_random_uuid(), p_security_id, p_ledger_id, p_price, 'USD', p_day, 'trade')
    ON CONFLICT (security_id, price_date) DO UPDATE
        SET price         = EXCLUDED.price,
            source        = 'trade',
            currency_code = EXCLUDED.currency_code
        WHERE security_prices.source IN ('import', 'simplefin', 'trade');

    RETURN QUERY SELECT p_security_id;
END;
$$;

GRANT EXECUTE ON FUNCTION security_price_upsert_from_trade(UUID, UUID, DATE, NUMERIC) TO coffer_app;

-- 3. One-time backfill from existing trade legs (ADR-0084 D5). Runs as
--    coffer_service (BYPASSRLS) under DbUp, so it sweeps every ledger's history
--    — including the imported funds. One `trade` row per (security, UTC-day),
--    taking the LAST trade of the day (h.seq DESC). Rank-gated identically to
--    the function so it replaces only import/simplefin/trade rows; a fetch/
--    manual price for the day survives. Template headers are excluded (ADR-0047)
--    — a reminder template is never a live cash event.
INSERT INTO security_prices (id, security_id, ledger_id, price, currency_code, price_date, source)
SELECT DISTINCT ON (l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date)
       gen_random_uuid(),
       l.security_id,
       l.ledger_id,
       l.unit_price,
       'USD',
       (h.posted_at AT TIME ZONE 'UTC')::date,
       'trade'
FROM txn_legs l
JOIN txn_headers h ON h.id = l.header_id
WHERE l.security_id IS NOT NULL
  AND l.quantity   IS NOT NULL
  AND l.quantity   <> 0
  AND l.unit_price IS NOT NULL
  AND l.unit_price > 0
  AND h.is_recurring_template = FALSE
ORDER BY l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date, h.seq DESC
ON CONFLICT (security_id, price_date) DO UPDATE
    SET price         = EXCLUDED.price,
        source        = 'trade',
        currency_code = EXCLUDED.currency_code
    WHERE security_prices.source IN ('import', 'simplefin', 'trade');

COMMIT;
