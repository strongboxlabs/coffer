-- ---------------------------------------------------------------------------
-- 241 — answer "which of this security's prices are unbacked" in ONE pass.
--
-- Migration 240 gave `fn_price_is_unbacked(ledger, security, date, source)`, a
-- per-ROW predicate. Correct, and the wrong shape: each call runs
-- fn_trade_price_for_day, which scans that security's legs for that one day. A
-- price list therefore rescans the same legs once per row.
--
-- Measured on a real security with 1,229 prices of which 348 are trade-source:
--
--   per-row predicate   728 ms
--   one pass             14 ms
--
-- 54x, for the same answer. The per-row form is kept — the paginated UI list
-- can still use it for a page of 50, and it reads clearly — but anything
-- covering a whole price history has to use this instead.
--
-- Returns the DATES rather than a boolean per row so a caller joins to it once:
-- the derived-day set is computed a single time for the security and the price
-- rows are anti-joined against it.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_unbacked_price_dates(
    p_ledger_id uuid, p_security_id uuid)
RETURNS TABLE (price_date date)
LANGUAGE sql
STABLE
AS $$
    WITH derived AS (
        SELECT DISTINCT (h.posted_at AT TIME ZONE 'UTC')::date AS d
        FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id   = p_ledger_id
          AND l.security_id = p_security_id
          AND h.is_recurring_template = FALSE
          AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                                  h.provider_raw_payload->>'qif_invst_action')
    )
    SELECT p.price_date
    FROM security_prices p
    WHERE p.ledger_id   = p_ledger_id
      AND p.security_id = p_security_id
      AND p.source = 'trade'
      AND NOT EXISTS (SELECT 1 FROM derived d WHERE d.d = p.price_date);
$$;

COMMENT ON FUNCTION fn_unbacked_price_dates(uuid, uuid) IS
    'Dates on which this security has a trade-source price that no trade '
    'currently derives. One pass over the security''s legs — the per-row '
    'fn_price_is_unbacked costs 54x as much across a full price history. '
    'Advisory: such a price may still be correct (ADR-0084 D4).';

GRANT EXECUTE ON FUNCTION fn_unbacked_price_dates(uuid, uuid)
    TO coffer_app, coffer_service;
