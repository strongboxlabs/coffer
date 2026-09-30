-- ---------------------------------------------------------------------------
-- 240 — a price nothing derives is ADVISORY, not a finding.
--
-- Migration 239 reported these as `orphaned` inside the consistency check,
-- alongside genuine disagreements. On a real ledger that produced a panel
-- reading "130 of 6540 no longer backed by a trade", with a Repair button that
-- would not have touched one of them, asking the reader to "decide" with
-- nothing to decide on. It was unreadable, and the framing was wrong before the
-- wording was.
--
-- WHY IT CANNOT BE A FINDING. `security_prices` records THAT a trade produced a
-- row (`source = 'trade'`) and never WHICH trade — the table has no link to a
-- transaction. So "orphaned" can only ever be inferred by re-deriving and
-- finding nothing, which cannot distinguish:
--
--   * a trade that was deleted — ADR-0084 D4 deliberately keeps the price, on
--     the grounds that a past execution was a real observation;
--   * a price the rule no longer produces because the RULE changed (237, 238);
--   * a trade edited so it no longer prices that day.
--
-- The first is correct behaviour. Reporting all three as defects marks a
-- healthy ledger broken. And this is not a backlog to be drained: deleting or
-- editing any priced trade creates one, so the category is permanently
-- non-empty by design.
--
-- So the check goes back to reporting only what it can stand behind —
-- `missing` and `value`, both repairable — and unbacked prices become a
-- separate advisory: a list of SECURITIES to look at, because a person acts on
-- a security, not on a date. Acting clears it either way: editing a price by
-- hand sets source = 'manual' (SecuritiesRepository), and deleting removes the
-- row, so neither can linger once dealt with.
-- ---------------------------------------------------------------------------

-- Back to findings the check can stand behind. Identical to 238's definition.
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

-- Is THIS stored price one nothing currently derives? Per-row, so the security
-- price list can mark its own rows without a second concept of the rule.
CREATE OR REPLACE FUNCTION fn_price_is_unbacked(
    p_ledger_id uuid, p_security_id uuid, p_price_date date, p_source text)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT p_source = 'trade'
       AND fn_trade_price_for_day(p_ledger_id, p_security_id, p_price_date) IS NULL;
$$;

COMMENT ON FUNCTION fn_price_is_unbacked(uuid, uuid, date, text) IS
    'Whether a stored price claims a trade produced it while no trade currently '
    'does. Advisory only: it may still be correct — ADR-0084 D4 keeps the price '
    'a deleted trade seeded — and security_prices records no link to the '
    'transaction that wrote it, so this can only ever be inferred.';

-- One row per security holding such prices, ordered by how much money the
-- security still represents: what you hold now, valued at its latest price.
-- Nothing is filtered out — a sold position sorts to the bottom at 0 rather
-- than disappearing, because "you no longer hold it" is not the same as "the
-- price does not matter", and a historical net-worth chart still reads it.
CREATE OR REPLACE FUNCTION fn_unbacked_price_securities(p_ledger_id uuid)
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
    SELECT p.security_id,
           count(*)                                   AS unbacked_count,
           min(p.price_date)                          AS earliest,
           max(p.price_date)                          AS latest,
           coalesce(
               (SELECT sum(h.quantity) FROM holdings h
                 WHERE h.security_id = p.security_id
                   AND h.ledger_id   = p_ledger_id), 0)
           * coalesce(
               (SELECT q.price FROM security_prices q
                 WHERE q.security_id = p.security_id
                 ORDER BY q.price_date DESC LIMIT 1), 0) AS holding_value
    FROM security_prices p
    WHERE p.ledger_id = p_ledger_id
      AND fn_price_is_unbacked(p.ledger_id, p.security_id, p.price_date, p.source)
    GROUP BY p.security_id
    ORDER BY holding_value DESC, unbacked_count DESC, p.security_id;
$$;

GRANT EXECUTE ON FUNCTION fn_price_is_unbacked(uuid, uuid, date, text)
    TO coffer_app, coffer_service;
GRANT EXECUTE ON FUNCTION fn_unbacked_price_securities(uuid)
    TO coffer_app, coffer_service;
