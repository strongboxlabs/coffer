-- ---------------------------------------------------------------------------
-- 239 — report a trade price nothing derives any more, instead of guessing.
--
-- Migrations 236, 237 and 238 each ended with a DELETE calibrated to one
-- ledger: `price BETWEEN 0.9 AND 1.1` and "the security trades above $5". Those
-- thresholds came from looking at two funds in one dataset. Measured against
-- that very dataset they address **1 of 130** orphaned trade prices — and on
-- someone else's install, whose placeholder figures are not near a dollar, they
-- would do nothing at all while 238 simultaneously removes the only thing that
-- was surfacing the problem.
--
-- The general statement needs no thresholds: a `trade`-source price on a day
-- where the rule derives nothing is not backed by anything. It was written by a
-- rule that no longer holds, or by a transaction that is gone.
--
-- REPORTED, NOT DELETED. Not every orphan is wrong: ADR-0084 D4 keeps the price
-- a since-deleted trade seeded, on the grounds that a past execution was a real
-- observation. Nothing in the row distinguishes that from a basis figure left
-- behind by an in-kind transfer, and a migration cannot ask. So the check names
-- them and the reader decides in the price list.
--
-- `expected_price` is the STORED price for these rows, not a derivation —
-- there is nothing to derive. Equal stored and expected is how a consumer
-- recognises that there is no difference to show, and the reason column carries
-- the meaning. `account_id` and `header_id` are NULL because no transaction
-- implies the row; that absence is also what tells the UI to link to the
-- security's price list rather than to a register.
-- ---------------------------------------------------------------------------

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
          AND h.is_recurring_template = FALSE
          AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                                  h.provider_raw_payload->>'qif_invst_action')
        ORDER BY l.security_id,
                 (h.posted_at AT TIME ZONE 'UTC')::date,
                 h.seq DESC, abs(l.amount) DESC, l.id DESC
    )
    -- Days the rule DOES derive: the stored row is missing, stale, or fine.
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
          AND p.price_date  = e.price_date

    UNION ALL

    -- Trade prices the rule derives nothing for. Reported so they can be
    -- judged; never repaired automatically, because some are legitimate
    -- (ADR-0084 D4) and no column says which.
    SELECT p.security_id,
           p.price_date,
           p.price,          -- no derivation exists; equal to stored by design
           p.price,
           p.source,
           'orphaned',
           NULL::uuid,
           NULL::uuid
    FROM security_prices p
    WHERE p.ledger_id = p_ledger_id
      AND p.source = 'trade'
      AND NOT EXISTS (SELECT 1 FROM expected e
                       WHERE e.security_id = p.security_id
                         AND e.price_date  = p.price_date);
$$;

COMMENT ON FUNCTION fn_trade_price_check(uuid) IS
    'Trade-derived security_prices against what the rule implies. reason NULL = '
    'agrees; missing / value = repairable; orphaned = a trade price nothing '
    'derives any more, reported for review and never repaired automatically '
    '(ADR-0084 D4 keeps some of them deliberately).';
