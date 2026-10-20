-- ---------------------------------------------------------------------------
-- 232 — a consistency check for the trade-derived `security_prices` projection.
--
-- The consistency checker covers four projections (balances, holdings, realized
-- gains, posting counts) and deliberately skipped the fifth. The stated reason
-- was sound: a trade leg seeds a price row, but the per-day source-priority rule
-- makes a MISSING row legitimate whenever a manual or fetched price already owns
-- that day, so a naive "is there a row?" check reports drift that is not there.
--
-- An EF interceptor keeps it in step on every save, as with the other four. What
-- was singular about it is that it was the one derived figure the report did not
-- examine — while feeding valuation, allocation and returns. Anything that
-- rewrites trade legs WITHOUT going through EF leaves it silently stale: a
-- hand-run scrub, or a migration that reshapes legs. That is exactly the shape
-- of the incident the checker exists for — a raw-SQL scrub desynced a projection
-- and the register showed wrong figures for months.
--
-- So the check is written the way the DTO comment said it would have to be —
-- against what the source-priority rule IMPLIES for each day, not against row
-- presence. It lives in SQL rather than LINQ because the rule needs DISTINCT ON,
-- and because a re-statement of the writer's logic in another language is one
-- more copy free to drift from the thing it is checking.
--
-- There are already three: migration 177's one-time backfill, its per-day
-- `security_price_upsert_from_trade`, and the importer's `TradePriceSeedStep`
-- (Dapper — the importer never touches EF, so the interceptor never fires for
-- it). This reader is the fourth, and deliberately so: a checker that CALLS the
-- writer cannot catch the writer being wrong. What it can catch is the three
-- writers ceasing to agree with each other. All four express the same rules:
--
--   * eligible legs: security_id set, quantity <> 0, unit_price > 0, and NOT a
--     recurring template (ADR-0047 — a template is never a live cash event);
--   * the day's price is the LAST trade by `h.seq`, not the max or the first;
--   * a `fetch` or `manual` row OUTRANKS a trade and legitimately survives, so
--     those days are not drift. Only import/simplefin/trade rows are comparable.
--
-- Returns one row per (security, day) the rule EXAMINES — not just the ones that
-- disagree — with `reason` NULL where the day is consistent. The caller needs
-- both numbers and only one scan should produce them: a projection that reports
-- "0 examined, 0 wrong" when it is healthy is indistinguishable from one whose
-- query silently returns nothing, and the consistency report's own guard is that
-- every projection examined something. Counting the examined days separately
-- would mean a second copy of the eligibility rule, which is the failure this
-- check exists to catch.
--
-- Comparison is at the DESTINATION scale: `security_prices.price` is
-- NUMERIC(19,4) and `txn_legs.unit_price` is NUMERIC(25,12), so the expected
-- value is rounded to 4dp before comparing — otherwise a finely-priced fill
-- would differ by the digits the column cannot hold, the repair would write the
-- only value it can, and the next check would report the same drift forever.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_trade_price_check(p_ledger_id uuid)
RETURNS TABLE (
    security_id    uuid,
    price_date     date,
    expected_price numeric,
    stored_price   numeric,
    stored_source  text,
    reason         text
)
LANGUAGE sql
STABLE
AS $$
    WITH expected AS (
        SELECT DISTINCT ON (l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date)
               l.security_id,
               (h.posted_at AT TIME ZONE 'UTC')::date AS price_date,
               round(l.unit_price, 4)                 AS price
        FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id = p_ledger_id
          AND l.security_id IS NOT NULL
          AND l.quantity   IS NOT NULL AND l.quantity   <> 0
          AND l.unit_price IS NOT NULL AND l.unit_price > 0
          AND h.is_recurring_template = FALSE
        ORDER BY l.security_id,
                 (h.posted_at AT TIME ZONE 'UTC')::date,
                 h.seq DESC
    )
    SELECT e.security_id,
           e.price_date,
           e.price,
           p.price,
           p.source,
           CASE
               -- A trade seeded nothing and nothing truer owns the day.
               WHEN p.security_id IS NULL THEN 'missing'
               -- A rank-comparable row holds a stale price: migration 177's
               -- writer would have overwritten it, so its surviving with the
               -- wrong value means a write was lost.
               WHEN p.source IN ('import', 'simplefin', 'trade')
                    AND p.price <> e.price THEN 'value'
               -- Consistent, or a fetch/manual close that outranks the trade
               -- and is SUPPOSED to disagree with it.
               ELSE NULL
           END AS reason
    FROM expected e
    LEFT JOIN security_prices p
           ON p.security_id = e.security_id
          AND p.price_date  = e.price_date;
$$;

COMMENT ON FUNCTION fn_trade_price_check(uuid) IS
    'One row per (security, day) that migration 177''s rank rule implies a '
    'trade-derived price for, with reason NULL where the stored state agrees. '
    'fetch/manual rows outrank a trade and never read as drift, which is why a '
    'naive row-presence check reports false positives.';

GRANT EXECUTE ON FUNCTION fn_trade_price_check(uuid) TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_trade_price_check(uuid) TO coffer_service;
