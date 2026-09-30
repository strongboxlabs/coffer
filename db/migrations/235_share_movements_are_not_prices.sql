-- ---------------------------------------------------------------------------
-- 235 — a share movement is not a price observation.
--
-- A Moneydance ShrsIn / ShrsOut moves shares with no cash consideration, so MD
-- has no purchase price to record and stores a NOMINAL amount equal to the
-- share count: one real record carries `1.pamt = 50631` ($506.31) against
-- `1.samt = -5063090` (506.309 shares). The importer derives
-- `unit_price = amount / quantity` and faithfully produces $1.000002 — which
-- then becomes a `trade` price in security_prices and values the holding.
--
-- ADR-0084 believed these were already excluded: "transfer_shares carries
-- pamt = 0 -> unit_price 0/NULL writes no price". That covers the zero case
-- only. A nominal, NON-zero amount passes `unit_price > 0` untouched.
--
-- Measured on a real ledger: 70 legs priced at the nominal, and 28 stored
-- `trade` prices between $0.90 and $1.10 for securities that trade above $5.
-- The consistency check called that ledger HEALTHY, because the writer and the
-- checker share the rule and computed the same wrong answer.
--
-- THE TEST IS NOT THE ACTION. 503 ShrsIn/ShrsOut legs on the same ledger carry
-- a real price — either MD recorded one or someone corrected it by hand — and
-- excluding the action wholesale would discard those. The signature of a
-- placeholder is that the amount still EQUALS the share count, i.e. the price
-- is $1/share because the dollars were never anything but the share count:
--
--     abs(amount) = round(abs(quantity), 2)
--
-- That also preserves a correction: a leg repriced to $20.3898 has an amount of
-- $10,323.54 against 506.309 shares and no longer matches, so it keeps pricing
-- its day. Only the untouched placeholders drop out.
--
-- A money-market fund genuinely trading at $1.00 satisfies the arithmetic, so
-- the action test is kept alongside it: both must hold. A money-market BUY is
-- not a ShrsIn and is unaffected.
--
-- This changes which prices EXIST rather than which of several is chosen, so it
-- is deliberately separate from migration 234 (which made the choice
-- deterministic). Stored rows already written from placeholders are not touched
-- here — see FU-048 for that, which is a data decision, not a rule.
-- ---------------------------------------------------------------------------

-- Is this leg a price observation at all? Shared by the rule and the checker so
-- the two cannot disagree about what counts.
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
       -- A share movement still priced at MD's nominal (dollars == shares) is
       -- not an execution; it is the absence of a price, written down.
       AND NOT (coalesce(p_provider_action, '') IN ('ShrsIn', 'ShrsOut')
                AND p_amount IS NOT NULL
                AND abs(p_amount) = round(abs(p_quantity), 2));
$$;

COMMENT ON FUNCTION fn_leg_prices_a_day(numeric, numeric, numeric, text) IS
    'Whether a security leg is a genuine price observation. Excludes a share '
    'movement (ShrsIn/ShrsOut) still carrying the importer''s nominal $1/share, '
    'which is the absence of a price rather than a price.';

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
      AND h.is_recurring_template = FALSE
      AND fn_leg_prices_a_day(l.quantity, l.unit_price, l.amount,
                              h.provider_raw_payload->>'qif_invst_action')
    ORDER BY h.seq DESC, abs(l.amount) DESC, l.id DESC
    LIMIT 1;
$$;

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
          AND fn_leg_prices_a_day(l.quantity, l.unit_price, l.amount,
                                  h.provider_raw_payload->>'qif_invst_action')
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

GRANT EXECUTE ON FUNCTION fn_leg_prices_a_day(numeric, numeric, numeric, text)
    TO coffer_app, coffer_service;
