-- ---------------------------------------------------------------------------
-- 234 — one deterministic definition of "the day's trade price", and the
--       writer starts using it.
--
-- Two defects, found by running migration 232's check against a real ledger.
--
-- 1. THE RULE WAS NOT DETERMINISTIC. "The last trade of the day by h.seq" has
--    no tie-break, and ONE header can carry many security legs at different
--    prices (a share transfer moves each lot at its own basis). Observed in
--    production: six legs on one seq at three distinct prices, where the stored
--    row held one and the check expected another. DISTINCT ON picks arbitrarily
--    among ties, so the two sides were not disagreeing about the data — they
--    were disagreeing about which coin flip to believe, and a repair could
--    re-report the same day forever without converging.
--
-- 2. THE WRITER NEVER APPLIED THE RULE. TradePriceFromLegInterceptor upserted
--    the price of whatever leg the save touched, without looking at the other
--    legs on that day. Correcting a 506-share leg therefore wrote its price,
--    while the checker — correctly applying the rule — derived the price of a
--    0.152-share fee fragment posted later the same day. Both were right by
--    their own rule. That is not drift; it is two different rules.
--
-- So the rule moves HERE, once, and everything else calls it:
--
--   fn_trade_price_for_day  — what the rule says the day's price is, or NULL.
--   fn_trade_price_reseed   — derive it and rank-gate it into security_prices.
--   fn_trade_price_check    — redefined to order identically.
--
-- ORDER BY h.seq DESC, abs(l.amount) DESC, l.id DESC
--   * h.seq       — the day's LAST trade, unchanged (ADR-0084).
--   * abs(amount) — among legs sharing a header, the largest by value is the
--                   most representative; a 15c fragment should not outrank a
--                   $10k execution merely by sharing its header.
--   * l.id        — a total order, so the answer can never vary between two
--                   runs over identical data. Without it the first two columns
--                   still tie whenever one header holds two legs of equal value.
--
-- WHAT THIS DOES NOT FIX. A Moneydance ShrsIn/ShrsOut carries a nominal ~$1/
-- share because a share movement has no purchase price, and the importer turns
-- that into unit_price. Those legs are still eligible here and still poison the
-- days they land on. Excluding them changes which prices exist rather than
-- which of several is chosen, so it is a separate decision (FU-048), and it is
-- the one that makes these figures CORRECT. This migration makes them STABLE,
-- which is the precondition for a repair anyone can trust.
-- ---------------------------------------------------------------------------

-- The rule. NULL when no eligible leg prices that day.
CREATE OR REPLACE FUNCTION fn_trade_price_for_day(
    p_ledger_id uuid, p_security_id uuid, p_day date)
RETURNS numeric
LANGUAGE sql
STABLE
AS $$
    SELECT round(l.unit_price, 4)
    FROM txn_legs l
    JOIN txn_headers h ON h.id = l.header_id
    WHERE l.ledger_id    = p_ledger_id
      AND l.security_id  = p_security_id
      AND (h.posted_at AT TIME ZONE 'UTC')::date = p_day
      AND l.quantity   IS NOT NULL AND l.quantity   <> 0
      AND l.unit_price IS NOT NULL AND l.unit_price > 0
      AND h.is_recurring_template = FALSE
    ORDER BY h.seq DESC, abs(l.amount) DESC, l.id DESC
    LIMIT 1;
$$;

COMMENT ON FUNCTION fn_trade_price_for_day(uuid, uuid, date) IS
    'The single definition of a day''s trade-derived price: last trade by seq, '
    'largest by value within a header, leg id as a total order. NULL when no '
    'eligible leg prices the day.';

-- Derive and write, rank-gated. The writer's entry point: callers name the
-- (security, day) they touched, not a price, so a save cannot seed a figure the
-- rule would not have chosen.
-- Returns a table rather than a scalar to match security_price_upsert_from_trade:
-- EF's HasDbFunction binding materialises a projection, and src/Api is LINQ-only
-- (audit-no-raw-sql.sh), so a scalar could not be invoked for its side effect.
CREATE OR REPLACE FUNCTION fn_trade_price_reseed(
    p_ledger_id uuid, p_security_id uuid, p_day date)
RETURNS TABLE (price numeric)
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_price numeric;
BEGIN
    v_price := fn_trade_price_for_day(p_ledger_id, p_security_id, p_day);
    IF v_price IS NULL THEN
        -- Every eligible leg is gone. ADR-0084 D4 keeps the existing row: a past
        -- execution was a real observation, and retracting it is a separate
        -- decision from this one. One row, NULL price, so the caller can tell
        -- "nothing to seed" from "function did not run".
        RETURN QUERY SELECT NULL::numeric;
        RETURN;
    END IF;
    PERFORM security_price_upsert_from_trade(
        p_ledger_id, p_security_id, p_day, v_price);
    RETURN QUERY SELECT v_price;
END;
$$;

COMMENT ON FUNCTION fn_trade_price_reseed(uuid, uuid, date) IS
    'Derive the day''s trade price by the one rule and rank-gate it into '
    'security_prices. Returns the price written, or NULL when no eligible leg '
    'prices the day (the existing row is left alone, ADR-0084 D4).';

-- The checker, ordering identically. Return type is unchanged from 233.
CREATE OR REPLACE FUNCTION fn_trade_price_check(p_ledger_id uuid)
RETURNS TABLE (
    security_id    uuid,
    price_date     date,
    expected_price numeric,
    stored_price   numeric,
    stored_source  text,
    reason         text,
    account_id     uuid,
    header_id      uuid
)
LANGUAGE sql
STABLE
AS $$
    WITH expected AS (
        SELECT DISTINCT ON (l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date)
               l.security_id,
               (h.posted_at AT TIME ZONE 'UTC')::date AS price_date,
               round(l.unit_price, 4)                 AS price,
               l.account_id,
               h.id                                   AS header_id
        FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id = p_ledger_id
          AND l.security_id IS NOT NULL
          AND l.quantity   IS NOT NULL AND l.quantity   <> 0
          AND l.unit_price IS NOT NULL AND l.unit_price > 0
          AND h.is_recurring_template = FALSE
        -- Identical to fn_trade_price_for_day. The two are a set-wide and a
        -- single-day reading of one rule; they must order the same way or the
        -- check disagrees with the writer it is checking.
        ORDER BY l.security_id,
                 (h.posted_at AT TIME ZONE 'UTC')::date,
                 h.seq DESC, abs(l.amount) DESC, l.id DESC
    )
    SELECT e.security_id,
           e.price_date,
           e.price,
           p.price,
           p.source,
           CASE
               WHEN p.security_id IS NULL THEN 'missing'
               WHEN p.source IN ('import', 'simplefin', 'trade')
                    AND p.price <> e.price THEN 'value'
               ELSE NULL
           END AS reason,
           e.account_id,
           e.header_id
    FROM expected e
    LEFT JOIN security_prices p
           ON p.security_id = e.security_id
          AND p.price_date  = e.price_date;
$$;

GRANT EXECUTE ON FUNCTION fn_trade_price_for_day(uuid, uuid, date) TO coffer_app, coffer_service;
GRANT EXECUTE ON FUNCTION fn_trade_price_reseed(uuid, uuid, date)  TO coffer_app, coffer_service;
