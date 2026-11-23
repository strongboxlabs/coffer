-- =============================================================================
-- 273 — a hidden or merged-away trade prices no day
-- =============================================================================
--
-- WHY
-- ---
-- A day's trade price is the rule's (fn_trade_price_for_day, migration 234; executions
-- only since 238): the last execution of the security that day. The valuations' rule
-- for a position with no price row (fn_last_trade_price, migration 259) reads the same
-- executions but leaves out three kinds of header, which are not trades in the ledger: a
-- recurring template, a hidden row (deleting an imported row hides it) and a row merged
-- into another (is_merged_into). The day's rule left out only the template. A hidden or
-- merged-away execution therefore kept pricing its day while it priced no valuation, and
-- deleting an imported trade kept its price on the day where deleting a manual one, which
-- removes the row, did not.
--
-- One rule now (ADR-0084, 2026-11-04 amendment): a trade is an execution on a header that
-- is not a template, not hidden and not merged into another, for the day's price exactly
-- as for valuation.
--
-- The unbacked-price advisory (fn_unbacked_price_securities) asked fn_price_is_unbacked
-- once per stored price, which runs the rule once per row: a cost of prices × trades
-- within a security, which a long price history takes past the API's command timeout.
-- The consistency report reads the advisory in the same request, so the report and the
-- scheduled check failed with it. A security's price list never had the cost: it reads
-- fn_unbacked_price_dates, which reads the security's trades once (migration 241).
--
-- WHAT CHANGES
-- ------------
-- The three functions that decide which executions price a (security, UTC day) read the
-- headers through live_txn_headers with fn_last_trade_price's filter,
-- h.is_hidden = FALSE AND h.is_merged_into IS NULL:
--
--   * fn_trade_price_for_day, the rule. fn_trade_price_reseed (every writer's entry
--     point) calls it, so it follows without being re-issued;
--   * fn_trade_price_check, the checker's set-wide statement of it;
--   * fn_unbacked_price_dates, the one-pass form of the advisory's test.
--
-- And the advisory reads its dates from the last of these:
--
--   * fn_unbacked_price_securities takes each security holding a trade-source price and
--     its unbacked dates from fn_unbacked_price_dates, one pass per security, where it
--     asked fn_price_is_unbacked per price row. Same columns, rows and order: a
--     trade-source price is unbacked exactly when the rule prices nothing on its day,
--     and the ranking by what the holding is worth now (migration 259) is unchanged.
--
-- fn_price_is_unbacked, the per-row test, has no caller after this, in SQL or in the
-- application, so it is dropped once the advisory no longer calls it.
--
-- Signatures, return types and volatility are unchanged, so CREATE OR REPLACE keeps each
-- function's grants and owner. The three COMMENTs are re-issued to state the rule, and the
-- advisory, which had none, gets one. fn_leg_prices_a_day (whether a leg is an execution)
-- and security_price_upsert_from_trade (the rank-gated write of a price it is given) read
-- no header and are unchanged.
--
-- EXISTING INSTALLS
-- -----------------
-- No row is written. A day whose stored price came from an execution an earlier version
-- hid or merged away, while another execution remains on the day, now disagrees with the
-- rule: the consistency check reports it once, as a trade-price value mismatch, and the
-- trade-price repair writes the price of the execution that remains. A day whose every
-- execution is hidden or merged away keeps its stored price (ADR-0084 D4), and the
-- unbacked-price advisory lists its security.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION fn_trade_price_for_day(
    p_ledger_id uuid, p_security_id uuid, p_day date)
RETURNS numeric
LANGUAGE sql
STABLE
AS $$
    SELECT round(l.unit_price, 4)
    FROM txn_legs l
    JOIN live_txn_headers h ON h.id = l.header_id
    WHERE l.ledger_id    = p_ledger_id
      AND l.security_id  = p_security_id
      AND (h.posted_at AT TIME ZONE 'UTC')::date = p_day
      -- A hidden or merged-away row is no trade in the ledger, as for fn_last_trade_price.
      AND h.is_hidden    = FALSE
      AND h.is_merged_into IS NULL
      AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                              h.provider_raw_payload->>'qif_invst_action')
    ORDER BY h.seq DESC, abs(l.amount) DESC, l.id DESC
    LIMIT 1;
$$;

COMMENT ON FUNCTION fn_trade_price_for_day(uuid, uuid, date) IS
    'Mig 273. The single definition of a day''s trade-derived price: among the day''s '
    'executions of the security (fn_leg_prices_a_day; hidden, merged and template '
    'headers are not trades, as for fn_last_trade_price), the last by seq, the largest '
    'by value within a header, leg id as a total order. NULL when no execution prices '
    'the day.';

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
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id = p_ledger_id
          AND l.security_id IS NOT NULL
          -- The rule's own filter (fn_trade_price_for_day), stated set-wide.
          AND h.is_hidden = FALSE
          AND h.is_merged_into IS NULL
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

COMMENT ON FUNCTION fn_trade_price_check(uuid) IS
    'Mig 273. Every (security, UTC day) the trade-price rule prices (fn_trade_price_for_day: '
    'executions only; hidden, merged and template headers are not trades), with the price '
    'it derives and the stored row. reason NULL = agrees, or a fetch or manual price owns '
    'the day; missing / value = repairable. A stored trade price no execution derives is '
    'not here: that is the unbacked-price advisory (ADR-0084 D6).';

CREATE OR REPLACE FUNCTION fn_unbacked_price_dates(
    p_ledger_id uuid, p_security_id uuid)
RETURNS TABLE (price_date date)
LANGUAGE sql
STABLE
AS $$
    WITH derived AS (
        SELECT DISTINCT (h.posted_at AT TIME ZONE 'UTC')::date AS d
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id   = p_ledger_id
          AND l.security_id = p_security_id
          -- The rule's own filter (fn_trade_price_for_day), in one pass.
          AND h.is_hidden = FALSE
          AND h.is_merged_into IS NULL
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
    'Mig 273. Dates on which this security has a trade-source price that no trade '
    'currently derives (fn_trade_price_for_day''s rule: executions only; hidden, merged '
    'and template headers are not trades). One pass over the security''s legs: a '
    'per-row test rescans them once per price row, a cost that grows with the length '
    'of the price history. '
    'Advisory: such a price may still be correct (ADR-0084 D4).';

-- The advisory, one pass per security. Identical to migration 259's body except that the
-- unbacked rows come from fn_unbacked_price_dates, asked laterally for each security that
-- holds a trade-source price, rather than from fn_price_is_unbacked per row. The advisory
-- states no rule of its own, and each security's trades are read once.
CREATE OR REPLACE FUNCTION fn_unbacked_price_securities(
    p_ledger_id uuid, p_scheduled_from timestamp with time zone)
RETURNS TABLE (
    security_id     uuid,
    unbacked_count  bigint,
    earliest        date,
    latest          date,
    holding_value   numeric
)
LANGUAGE sql
STABLE
AS $$
    WITH held AS (
        SELECT n.security_id,
               sum(CASE WHEN n.price IS NULL THEN n.cost_basis
                        ELSE round(n.quantity * n.price, 4)
                   END) AS value_now
          FROM holdings_as_of_now(p_ledger_id, p_scheduled_from, NULL) n
         GROUP BY n.security_id
    ),
    unbacked AS (
        SELECT s.security_id, d.price_date
          FROM (SELECT DISTINCT p.security_id
                  FROM security_prices p
                 WHERE p.ledger_id = p_ledger_id
                   AND p.source = 'trade') s
         CROSS JOIN LATERAL fn_unbacked_price_dates(p_ledger_id, s.security_id) d
    )
    SELECT u.security_id,
           count(*)                         AS unbacked_count,
           min(u.price_date)                AS earliest,
           max(u.price_date)                AS latest,
           coalesce(max(held.value_now), 0) AS holding_value
    FROM unbacked u
    LEFT JOIN held ON held.security_id = u.security_id
    GROUP BY u.security_id
    ORDER BY holding_value DESC, unbacked_count DESC, u.security_id;
$$;

COMMENT ON FUNCTION fn_unbacked_price_securities(uuid, timestamp with time zone) IS
    'Mig 273. The unbacked-price advisory: one row per security holding a trade-source '
    'price no trade currently derives, with how many, the first and last such date, and '
    'what the holding is worth on the caller''s today (holdings_as_of_now, at its price '
    'or else the cost basis; 0 when not held), largest first. Its dates come from '
    'fn_unbacked_price_dates, one pass per security. Advisory, never a finding '
    '(ADR-0084 D6).';

-- The per-row test (migration 240). Nothing calls it now that the advisory above reads
-- fn_unbacked_price_dates, and a function nothing calls is a second statement of the
-- advisory's test that nothing keeps in step with the first.
DROP FUNCTION IF EXISTS fn_price_is_unbacked(uuid, uuid, date, text);

COMMIT;
