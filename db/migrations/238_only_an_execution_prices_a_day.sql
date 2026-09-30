-- ---------------------------------------------------------------------------
-- 238 — only an EXECUTION prices a day. The rule stops being a blacklist.
--
-- Third correction to one principle (ADR-0084 D6: a share movement is not a
-- price), and the reason there was a third is structural, not a missed case.
--
-- 235 excluded a Moneydance ShrsIn/ShrsOut priced at the importer's nominal.
-- 237 widened that to those actions whatever figure they carry. Both keyed on
-- `provider_raw_payload->>'qif_invst_action'` — the MONEYDANCE spelling — and
-- so neither could see Coffer's own: `convert_in_kind_transfer` deletes the
-- imported pair and recreates it natively, giving a header with
-- `action = 'transfer_shares'` and no provider payload at all. Its legs are
-- priced by the FIFO plan at each lot's carried basis; LedgerActions says so in
-- as many words ("moves FIFO lots + cost basis source -> destination with zero
-- realized gain").
--
-- Each fix was a new entry on a blacklist, found in production. A blacklist
-- FAILS OPEN: an action nobody enumerated prices days by default, so the next
-- unconsidered one is the next incident. Inverting it is the actual fix.
--
-- WHAT PRICES A DAY. Enumerated from every (action, provider_action) pair that
-- carries a priced security leg on a real ledger — 18 of them:
--
--   buy   / (native), Buy                      a purchase
--   buyx  / (native), Buy, BuyX                a purchase that also moved cash
--   sell  / (native), Sell                     a sale
--   sellx / (native), SellX                    a sale that also moved cash
--   dividend_reinvest / (native), ReinvDiv,    a reinvestment, executed at NAV
--                       ReinvInt, ReinvLg,
--                       ReinvSh, ReinvMd
--
-- and the two that must not, which the same enumeration exposes:
--
--   buyx  / ShrsIn      443 legs   shares in, no consideration
--   sell  / ShrsOut       8 legs
--   sellx / ShrsOut      52 legs
--   transfer_shares/*              in-kind move, legs carry lot basis
--
-- Note `buyx` appears on both sides: it is the mapping target for a real BuyX
-- (303 legs) AND for ShrsIn (443). The Coffer action alone cannot decide, which
-- is why both fields are arguments.
--
-- A new action now prices nothing until someone says it should. That is the
-- safe direction: a missing price shows as a stale valuation, a wrong one
-- silently mis-values a holding and — as this sequence proved — the consistency
-- check cannot see it, because the checker shares the rule.
--
-- WHY THE IN-KIND CASE WAS NOT CAUGHT BEFORE SHIPPING TWICE. The dev dataset
-- contains zero `transfer_shares` legs; its in-kind transfers were never
-- converted. No measurement against dev could show it. It is covered
-- synthetically in db/test/verify_share_movements_are_not_prices.sql, which is
-- the only place it can be exercised here.
--
-- `p_amount` goes: 237 removed the arithmetic that used it, and keeping a
-- parameter "for a future provider" was speculation.
-- ---------------------------------------------------------------------------

DROP FUNCTION IF EXISTS fn_leg_prices_a_day(numeric, numeric, numeric, text);

CREATE FUNCTION fn_leg_prices_a_day(
    p_quantity        numeric,
    p_unit_price      numeric,
    p_action          text,
    p_provider_action text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT p_quantity   IS NOT NULL AND p_quantity   <> 0
       AND p_unit_price IS NOT NULL AND p_unit_price > 0
       -- An execution, and nothing else. Anything not named here prices no day
       -- until it is named — including an action that does not exist yet.
       AND coalesce(p_action, '') IN ('buy', 'buyx', 'sell', 'sellx',
                                      'dividend_reinvest')
       -- ...except where the provider says the shares simply moved. ShrsIn maps
       -- onto buyx and ShrsOut onto sell/sellx, so the action above admits them
       -- and only the provider field can tell them apart.
       AND coalesce(p_provider_action, '') NOT IN ('ShrsIn', 'ShrsOut');
$$;

COMMENT ON FUNCTION fn_leg_prices_a_day(numeric, numeric, text, text) IS
    'Whether a security leg is a genuine price observation. An allow-list of '
    'executions: buy/buyx/sell/sellx/dividend_reinvest, minus the Moneydance '
    'ShrsIn/ShrsOut that map onto them. Anything else — transfer_shares, or an '
    'action added later — prices no day until it is named here. Fails closed on '
    'purpose: a missing price is a stale valuation, a wrong one silently '
    'mis-values a holding and the checker cannot see it.';

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
      AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
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
          AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
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

GRANT EXECUTE ON FUNCTION fn_leg_prices_a_day(numeric, numeric, text, text)
    TO coffer_app, coffer_service;

-- Cleanup on the same narrow terms as 236 and 237: a trade-source price the
-- widened rule can no longer account for, at a figure that cannot be true for
-- the security. A plausible orphan is left alone (ADR-0084 D4).
DELETE FROM security_prices p
WHERE p.source = 'trade'
  AND p.price BETWEEN 0.9 AND 1.1
  AND fn_trade_price_for_day(p.ledger_id, p.security_id, p.price_date) IS NULL
  AND EXISTS (
        SELECT 1 FROM security_prices q
        WHERE q.security_id = p.security_id
          AND q.price > 5);
