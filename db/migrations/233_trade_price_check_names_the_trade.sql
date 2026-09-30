-- ---------------------------------------------------------------------------
-- 233 — fn_trade_price_check also returns WHICH TRADE decided the day.
--
-- Migration 232 returned the security, the day and the two prices. On the first
-- real ledger it ran against, that read as:
--
--   security 9705c7fa-6ed7-4ddf-98d3-37ba47acddbb on 2006-01-06 ·
--   security_prices.price: stored 26.1894, expected 25.7692
--
-- which is not something a person can act on. To decide whether to repair, the
-- reader has to find the trade behind the expected figure and judge whether it
-- is a real execution — and nothing in the row says where to look. Two of the
-- three things needed were missing: the account the trade sits on, and the
-- transaction itself.
--
-- They are free here. The rule already picks ONE determining leg per
-- (security, day) — the last trade by `h.seq` — so the leg's account and its
-- header are right there in the DISTINCT ON; 232 simply threw them away.
--
-- Returning the header matters most on the case this check was found to catch:
-- ADR-0084 D4 says a DELETE does not retract the price row a trade seeded, so a
-- day whose trades were later corrected keeps a price no surviving trade
-- implies. The reader has to compare the stale figure against the trade that is
-- still there, and cannot do that without being told which one that is.
--
-- The return type changes, so this DROPs and recreates rather than REPLACEing —
-- `CREATE OR REPLACE FUNCTION` cannot alter a function's output columns. 232 is
-- released and stays as written (engineering-standards §3.1).
-- ---------------------------------------------------------------------------

DROP FUNCTION IF EXISTS fn_trade_price_check(uuid);

CREATE FUNCTION fn_trade_price_check(p_ledger_id uuid)
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
               -- A rank-comparable row holds a price the day's last trade does
               -- not imply: migration 177's writer would have overwritten it, so
               -- either a write was lost or the trade behind it has since been
               -- corrected away (ADR-0084 D4 leaves the row behind on delete).
               WHEN p.source IN ('import', 'simplefin', 'trade')
                    AND p.price <> e.price THEN 'value'
               -- Consistent, or a fetch/manual close that outranks the trade
               -- and is SUPPOSED to disagree with it.
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
    'One row per (security, day) that migration 177''s rank rule implies a '
    'trade-derived price for, with reason NULL where the stored state agrees, '
    'plus the account and header of the trade that decided the day. fetch/manual '
    'rows outrank a trade and never read as drift.';

GRANT EXECUTE ON FUNCTION fn_trade_price_check(uuid) TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_trade_price_check(uuid) TO coffer_service;
