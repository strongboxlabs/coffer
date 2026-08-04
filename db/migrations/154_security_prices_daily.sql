-- =============================================================================
-- 154 — daily closing-price model (ADR-0070)
-- =============================================================================
--
-- security_prices was timestamp-keyed (price_date TIMESTAMPTZ, UNIQUE on the
-- exact timestamp), so a security could hold several rows for one calendar day:
-- a Yahoo EOD close (midnight UTC), a SimpleFIN intraday balance (the brokerage
-- balance-date, in seconds — both tagged 'fetch'), and repeat syncs. We want
-- exactly one row per (security, day) = that day's closing price.
--
-- This migration collapses price_date to a calendar DATE and introduces the
-- source-priority ladder (ADR-0070 D2):
--     manual (2) == Yahoo/fetch (2)  >  simplefin (1)  >  import (0)
-- Going forward each writer upserts per (security, day) honoring that rank; the
-- step below is the one-time cleanup of the historical timestamp-keyed rows.
-- =============================================================================

BEGIN;

-- 1. Dedup to one row per (security, UTC day), keeping the ladder winner. For
--    the existing data (sources 'import' + 'fetch' only), rank by:
--      * 'manual'                         -> 2  (future-proof; none exist yet)
--      * 'fetch' at midnight UTC (Yahoo)  -> 2
--      * 'fetch' intraday (SimpleFIN) / 'simplefin' -> 1
--      * 'import' (the MD seed)           -> 0
--    Ties (e.g. a manual + Yahoo close on the same day) break by latest. UTC-day
--    bucketing matches the column collapse in step 2.
DELETE FROM security_prices sp
USING (
    SELECT id,
           ROW_NUMBER() OVER (
               PARTITION BY security_id, (price_date AT TIME ZONE 'UTC')::date
               ORDER BY
                   CASE
                       WHEN source = 'manual' THEN 2
                       WHEN source = 'fetch'
                            AND (price_date AT TIME ZONE 'UTC')::time = TIME '00:00:00' THEN 2
                       WHEN source IN ('fetch', 'simplefin') THEN 1
                       ELSE 0   -- import
                   END DESC,
                   price_date DESC
           ) AS rn
    FROM security_prices
) ranked
WHERE sp.id = ranked.id
  AND ranked.rn > 1;

-- 2. Collapse the column to a calendar date (UTC day). The dependent indexes —
--    including UNIQUE (security_id, price_date) — rebuild on the DATE values, so
--    uniqueness now means one row per (security, day). Step 1 guarantees the
--    rebuild can't hit a duplicate.
ALTER TABLE security_prices
    ALTER COLUMN price_date TYPE DATE USING (price_date AT TIME ZONE 'UTC')::date;

-- 3. SimpleFIN becomes its own source so it can rank below Yahoo (ADR-0070 D3).
--    History stays tagged 'fetch' — harmless: Yahoo still wins a future rank tie
--    and SimpleFIN never outranks it.
ALTER TABLE security_prices
    DROP CONSTRAINT ck_security_prices_source;
ALTER TABLE security_prices
    ADD CONSTRAINT ck_security_prices_source
        CHECK (source IN ('import', 'fetch', 'manual', 'simplefin'));

COMMIT;
