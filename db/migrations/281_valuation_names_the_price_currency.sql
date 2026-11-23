-- =============================================================================
-- 281 — every valuation names the currency of the price it used (ADR-0114)
-- =============================================================================
--
-- WHY
-- ---
-- A position is valued at a price, and a price is in a currency: a close's is its
-- security_prices.currency_code (ADR-0114 D5), a trade's is the currency of the
-- brokerage that made the trade (fn_trade_currency, migration 274). No valuation read
-- either. The current-value and as-of feeders returned quantity times price and
-- nothing else, so a position priced in one currency was added to its account in
-- another, even on a ledger where every account agrees.
--
-- A position now converts from its price's currency into its account's currency at
-- the day's rate, and the account then converts as a whole (ADR-0114 D7). That
-- conversion is the API's: it reads every rate through one lookup, and nothing here
-- reads fx_rates. What this migration adds is the currency each valuation's price is
-- in, so the API can tell which positions need converting.
--
-- WHAT CHANGES
-- ------------
--   * fn_last_trade_price gains currency_code: fn_trade_currency's for the picked
--     execution's line, the currency migration 274 writes a trade price in.
--   * security_prices_as_of_now gains price_currency: the close's currency_code, else
--     the last trade's currency_code.
--   * holdings_as_of_now gains price_currency: security_prices_as_of_now's for the
--     position's security, NULL when it has no price (the position is then carried at
--     its cost basis, which is in the account's currency).
--   * holdings_market_value_as_of_set gains price_currency: the forward-filled close's
--     currency_code, else the trade's, NULL for a position carried at cost basis.
--
-- Nothing else in any body changes: each is its last definition (259) with the column
-- added beside the price it describes. The output columns change, so each is dropped
-- and created again, and its grants and comment re-issued. The callers are string-bodied
-- SQL (holdings_as_of_now, fn_unbacked_price_securities, security_prices_as_of_now,
-- holdings_market_value_as_of_set), which hold no dependency on them and resolve the
-- new function on their next call; every column they read is still there.
--
-- A close rounded or split-adjusted keeps its currency; a split changes a price's
-- basis, never its unit.
--
-- No data is read or written.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. The one no-price rule names its currency.
-- ---------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_last_trade_price(uuid, uuid, timestamp with time zone);

CREATE FUNCTION public.fn_last_trade_price(p_ledger_id uuid, p_security_id uuid, p_as_of timestamp with time zone)
 RETURNS TABLE(price numeric, traded_at timestamp with time zone, currency_code text)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
SELECT t.price,
       t.traded_at,
       -- The currency migration 274 writes a trade price in: the brokerage's that made
       -- the trade, read for the one picked line only.
       (SELECT c.currency_code FROM fn_trade_currency(t.account_id) c) AS currency_code
  FROM (
      SELECT round(l.unit_price, 4) AS price,
             h.posted_at            AS traded_at,
             l.account_id
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
       WHERE l.ledger_id    = p_ledger_id
         AND l.security_id  = p_security_id
         AND h.posted_at   <= p_as_of
         AND h.is_hidden    = FALSE
         AND h.is_merged_into IS NULL
         -- What ADR-0084 lets price a day: an execution, never a share movement.
         AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                                 h.provider_raw_payload->>'qif_invst_action')
       -- ADR-0084's total order (mig 234), behind the date: the latest trade, and within
       -- a header the largest leg, so the answer cannot vary between runs.
       ORDER BY h.posted_at DESC, h.seq DESC, abs(l.amount) DESC, l.id DESC
       LIMIT 1) t;
$function$;

GRANT EXECUTE ON FUNCTION fn_last_trade_price(uuid, uuid, timestamp with time zone)
    TO coffer_app, coffer_service;

COMMENT ON FUNCTION fn_last_trade_price(uuid, uuid, timestamp with time zone) IS
    'Mig 259, ADR-0084 amendment: the unit price of a security''s most recent '
    'execution in the ledger at or before p_as_of (fn_leg_prices_a_day; hidden, '
    'merged and template headers excluded), rounded to 4 places as a trade price is '
    'written, with its instant and (mig 281) its currency, fn_trade_currency''s for the '
    'line. No row when it never traded at a price. The price of a position with no '
    'security_prices row on or before the date, for every valuation, current and '
    'historical; with neither, a position is carried at cost.';

-- ---------------------------------------------------------------------------
-- 2. The price a current value is computed at, with its currency.
-- ---------------------------------------------------------------------------
DROP FUNCTION IF EXISTS security_prices_as_of_now(uuid, timestamp with time zone, uuid[]);

CREATE FUNCTION public.security_prices_as_of_now(p_ledger_id uuid, p_scheduled_from timestamp with time zone, p_security_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(security_id uuid, price numeric, price_date date, price_currency text)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
WITH bound AS (
    -- The last instant the register does not call Scheduled.
    SELECT p_scheduled_from - interval '1 microsecond' AS as_of
)
SELECT s.id AS security_id,
       -- Back-adjusted onto the day's split basis, by the rule
       -- holdings_market_value_as_of_set applies at any instant (mig 230): a price
       -- observed before a split that has since happened is on the pre-split basis,
       -- and a split-adjusted quantity times it would count the split twice. ROUND
       -- bounds the NUMERIC scale, as there.
       CASE WHEN f.factor > 0 AND f.factor <> 1
            THEN ROUND(o.price / f.factor, 12)
            ELSE o.price
       END AS price,
       o.price_date,
       o.price_currency
  FROM securities s
  CROSS JOIN bound b
  -- Tier 1: the latest close dated on or before the caller's today. price_date is
  -- a UTC calendar day (ADR-0070 D5), so the day is taken in UTC.
  LEFT JOIN LATERAL (
      SELECT sp.price, sp.price_date, sp.currency_code
        FROM security_prices sp
       WHERE sp.security_id = s.id
         AND sp.ledger_id   = p_ledger_id
         AND sp.price_date <= (b.as_of AT TIME ZONE 'UTC')::date
       ORDER BY sp.price_date DESC
       LIMIT 1
  ) fd ON true
  -- Tier 2, only without a close: the last trade's price, the one no-price rule.
  -- Asked with a NULL security when there IS a close, which matches no leg, so a
  -- priced security costs no scan of its trades: the sidebar reads this on every
  -- page, and a join condition alone would not stop the lateral from running.
  LEFT JOIN LATERAL fn_last_trade_price(
                p_ledger_id, CASE WHEN fd.price IS NULL THEN s.id END, b.as_of) tr
         ON true
  CROSS JOIN LATERAL (
      SELECT COALESCE(fd.price, tr.price) AS price,
             COALESCE(fd.price_date, (tr.traded_at AT TIME ZONE 'UTC')::date) AS price_date,
             -- The currency of the price used: the close's, else the trade's.
             CASE WHEN fd.price IS NOT NULL THEN fd.currency_code ELSE tr.currency_code END
                 AS price_currency,
             -- A same-day split is already in that day's close, so a close's
             -- boundary is the start of the following day; a trade's is its instant.
             CASE WHEN fd.price IS NOT NULL
                  THEN (fd.price_date + 1)::timestamptz
                  ELSE tr.traded_at
             END AS obs_at
  ) o
  CROSS JOIN LATERAL (
      SELECT numeric_product(ARRAY(
                 SELECT ss.ratio
                   FROM security_splits ss
                  WHERE ss.ledger_id   = p_ledger_id
                    AND ss.security_id = s.id
                    AND ss.split_at    > o.obs_at
                    AND ss.split_at   <= b.as_of)) AS factor
  ) f
 WHERE s.ledger_id = p_ledger_id
   AND (p_security_ids IS NULL OR s.id = ANY (p_security_ids))
   AND o.price IS NOT NULL;
$function$;

GRANT EXECUTE ON FUNCTION security_prices_as_of_now(uuid, timestamp with time zone, uuid[])
    TO coffer_app, coffer_service;

COMMENT ON FUNCTION security_prices_as_of_now(uuid, timestamp with time zone, uuid[]) IS
    'Mig 259: per security, the latest security_prices row dated on or before the '
    'caller''s today (the day before p_scheduled_from, the register''s Scheduled '
    'boundary), else fn_last_trade_price at the end of that day, back-adjusted onto '
    'the day''s split basis for any split after the observation (the '
    'holdings_market_value_as_of_set rule). price_date is the close''s date or the '
    'trade''s UTC day; price_currency (mig 281) the close''s currency_code or the '
    'trade''s. No row for a security with neither. The price every current value is '
    'computed at.';

-- ---------------------------------------------------------------------------
-- 3. What is held today, with the currency of the price it is valued at.
-- ---------------------------------------------------------------------------
DROP FUNCTION IF EXISTS holdings_as_of_now(uuid, timestamp with time zone, uuid[]);

CREATE FUNCTION public.holdings_as_of_now(p_ledger_id uuid, p_scheduled_from timestamp with time zone, p_account_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(account_id uuid, security_id uuid, quantity numeric, cost_basis numeric, price numeric, price_date date, price_currency text)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
-- Holdings accounts whose projection includes something the register calls
-- Scheduled: a security leg on a header at or after the boundary, or such a split
-- of a security they hold. The headers are the ones the projection counts
-- (live_txn_headers drops recurring templates, ADR-0048; hidden and merged headers
-- are dropped as the walk drops them), so an account is walked only where the
-- projection and today differ. Over-including one would cost a walk, never a wrong
-- figure.
WITH moved AS (
    SELECT l.account_id
      FROM live_txn_headers h
      JOIN txn_legs l ON l.header_id = h.id
     WHERE h.ledger_id = p_ledger_id
       AND h.posted_at >= p_scheduled_from
       AND h.is_hidden = FALSE
       AND h.is_merged_into IS NULL
       AND l.security_id IS NOT NULL
       AND l.quantity IS NOT NULL
    UNION
    SELECT ho.account_id
      FROM security_splits ss
      JOIN holdings ho ON ho.security_id = ss.security_id
     WHERE ss.ledger_id = p_ledger_id
       AND ss.split_at >= p_scheduled_from
),
moved_in_scope AS (
    SELECT m.account_id
      FROM moved m
     WHERE p_account_ids IS NULL OR m.account_id = ANY (p_account_ids)
),
positions AS (
    SELECT ho.account_id, ho.security_id, ho.quantity, ho.cost_basis
      FROM holdings ho
     WHERE ho.ledger_id = p_ledger_id
       AND ho.quantity <> 0
       AND (p_account_ids IS NULL OR ho.account_id = ANY (p_account_ids))
       AND ho.account_id NOT IN (SELECT m.account_id FROM moved m)
    UNION ALL
    -- The as-of walk, bounded at the last instant not Scheduled. The EXISTS is a
    -- one-time gate: with nothing moved, the walk is not entered at all.
    SELECT w.account_id, w.security_id, w.quantity, w.cost_basis
      FROM holdings_cost_basis_as_of(
               p_ledger_id,
               p_scheduled_from - interval '1 microsecond',
               ARRAY(SELECT s.account_id FROM moved_in_scope s)) w
     WHERE EXISTS (SELECT 1 FROM moved_in_scope)
)
SELECT p.account_id,
       p.security_id,
       p.quantity,
       p.cost_basis,
       px.price,
       px.price_date,
       px.price_currency
  FROM positions p
  LEFT JOIN security_prices_as_of_now(
                p_ledger_id, p_scheduled_from,
                ARRAY(SELECT DISTINCT q.security_id FROM positions q)) px
         ON px.security_id = p.security_id;
$function$;

GRANT EXECUTE ON FUNCTION holdings_as_of_now(uuid, timestamp with time zone, uuid[])
    TO coffer_app, coffer_service;

COMMENT ON FUNCTION holdings_as_of_now(uuid, timestamp with time zone, uuid[]) IS
    'Mig 259: positions held on the caller''s today per (holdings account, security), '
    'quantity and FIFO cost basis, with the price security_prices_as_of_now gives (a '
    'close on or before that day, else the last trade''s price, on the day''s split '
    'basis; NULL when the security has neither) and (mig 281) that price''s currency. '
    'Counts exactly what the register does not call Scheduled (p_scheduled_from). Read '
    'from the holdings projection except for accounts with an event at or after the '
    'boundary, which are walked by holdings_cost_basis_as_of. The current-value readers '
    'use it; the projection itself still describes every event entered.';

-- ---------------------------------------------------------------------------
-- 4. The net-worth history's feeder names the currency of each position's price.
--    Identical to migration 259's body but for the currency carried beside the price.
-- ---------------------------------------------------------------------------
DROP FUNCTION IF EXISTS holdings_market_value_as_of_set(uuid, timestamp with time zone[], uuid[]);

CREATE FUNCTION public.holdings_market_value_as_of_set(p_ledger_id uuid, p_as_ofs timestamp with time zone[], p_account_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(as_of timestamp with time zone, account_id uuid, security_id uuid, quantity numeric, market_value numeric, priced_from text, price_currency text)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
WITH asks AS (
    SELECT DISTINCT u AS at
    FROM unnest(p_as_ofs) AS u
    WHERE u IS NOT NULL
),
horizon AS (
    SELECT MAX(at) AS t_max FROM asks
),
-- Every holdings-side leg up to the LAST requested instant. Discovery is bounded
-- by t_max rather than per instant; a position not yet started at an earlier
-- instant accumulates to zero there and is dropped by the qty <> 0 filter, exactly
-- as mig 172 skips it.
raw_legs AS (
    SELECT l.account_id,
           l.security_id,
           l.id           AS leg_id,
           h.posted_at AS posted_at,
           l.quantity,
           CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
    FROM txn_legs l
    JOIN live_txn_headers h ON h.id = l.header_id
    CROSS JOIN horizon
    WHERE l.ledger_id      = p_ledger_id
      AND l.security_id   IS NOT NULL
      AND l.quantity      IS NOT NULL
      AND h.posted_at     <= horizon.t_max
      AND h.is_hidden      = FALSE
      AND h.is_merged_into IS NULL
      AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
),
positions AS (
    SELECT DISTINCT account_id, security_id FROM raw_legs
),
held_securities AS (
    SELECT DISTINCT security_id FROM raw_legs
),
-- Splits per security as parallel arrays, bounded to t_max. Tiny by nature, and it
-- makes every per-leg and per-instant factor an array unnest rather than a probe.
split_arr AS (
    SELECT ss.security_id,
           array_agg(ss.ratio    ORDER BY ss.split_at) AS ratios,
           array_agg(ss.split_at ORDER BY ss.split_at) AS split_ats
    FROM security_splits ss
    CROSS JOIN horizon
    WHERE ss.ledger_id = p_ledger_id
      AND ss.split_at <= horizon.t_max
    GROUP BY ss.security_id
),
-- (1) Restate each leg into t_max's split basis.
adjusted AS (
    SELECT rl.account_id,
           rl.security_id,
           rl.leg_id,
           rl.posted_at,
           rl.sort_class,
           rl.quantity * numeric_product(
               CASE WHEN sa.ratios IS NULL THEN NULL::NUMERIC[]
               ELSE ARRAY(
                   SELECT r FROM unnest(sa.ratios, sa.split_ats) AS u(r, sat)
                   WHERE sat > rl.posted_at)
               END) AS adj_quantity
    FROM raw_legs rl
    LEFT JOIN split_arr sa ON sa.security_id = rl.security_id
),
-- Legs and instants in one stream. sort_class 9 puts an instant after every real
-- leg sharing it, which is what `posted_at <= p_as_of` means.
qty_stream AS (
    SELECT account_id, security_id, posted_at AS event_at, sort_class, leg_id,
           adj_quantity, NULL::TIMESTAMPTZ AS ask_at
    FROM adjusted
    UNION ALL
    SELECT p.account_id, p.security_id, a.at, 9, NULL::UUID, 0::NUMERIC, a.at
    FROM positions p
    CROSS JOIN asks a
),
qty_running AS (
    SELECT s.account_id, s.security_id, s.ask_at,
           SUM(s.adj_quantity) OVER (
               PARTITION BY s.account_id, s.security_id
               ORDER BY s.event_at, s.sort_class, s.leg_id NULLS FIRST
           ) AS cum_adj_quantity
    FROM qty_stream s
),
-- Read the running total at each instant and divide out the splits not yet due.
held AS (
    SELECT r.ask_at AS at,
           r.account_id,
           r.security_id,
           r.cum_adj_quantity
               / NULLIF(numeric_product(
                     CASE WHEN sa.ratios IS NULL THEN NULL::NUMERIC[]
                     ELSE ARRAY(
                         SELECT rr FROM unnest(sa.ratios, sa.split_ats) AS u(rr, sat)
                         WHERE sat > r.ask_at)
                     END), 0) AS quantity
    FROM qty_running r
    LEFT JOIN split_arr sa ON sa.security_id = r.security_id
    WHERE r.ask_at IS NOT NULL
),
holdings_at AS (
    SELECT * FROM held WHERE quantity IS NOT NULL AND quantity <> 0
),
-- (2) Feed price by forward fill, at DATE granularity to match mig 172. The close's
-- currency travels with it (mig 281).
ask_dates AS (
    SELECT at, at::date AS on_date FROM asks
),
feed_stream AS (
    SELECT sp.security_id, sp.price_date AS on_date, 0 AS is_ask,
           sp.price, sp.currency_code, NULL::TIMESTAMPTZ AS ask_at
    FROM security_prices sp
    CROSS JOIN horizon
    WHERE sp.ledger_id   = p_ledger_id
      AND sp.price_date <= horizon.t_max::date
      AND sp.security_id IN (SELECT security_id FROM held_securities)
    UNION ALL
    SELECT hs.security_id, ad.on_date, 1, NULL::NUMERIC, NULL::TEXT, ad.at
    FROM held_securities hs
    CROSS JOIN ask_dates ad
),
feed_islands AS (
    SELECT security_id, on_date, is_ask, price, currency_code, ask_at,
           COUNT(price) OVER (
               PARTITION BY security_id ORDER BY on_date, is_ask
           ) AS island
    FROM feed_stream
),
feed_at AS (
    SELECT security_id, ask_at, ff_price, ff_date, ff_currency
    FROM (
        SELECT security_id, ask_at,
               FIRST_VALUE(price)         OVER w AS ff_price,
               FIRST_VALUE(on_date)       OVER w AS ff_date,
               FIRST_VALUE(currency_code) OVER w AS ff_currency
        FROM feed_islands
        WINDOW w AS (PARTITION BY security_id, island ORDER BY on_date, is_ask)
    ) x
    WHERE x.ask_at IS NOT NULL
),
needs_trade AS (
    SELECT h.at, h.account_id, h.security_id
    FROM holdings_at h
    LEFT JOIN feed_at f ON f.security_id = h.security_id AND f.ask_at = h.at
    WHERE f.ff_price IS NULL
),
-- (3) No close: the security's last execution in the ledger at or before the
-- instant, by the one no-price rule (fn_last_trade_price, mig 259). Asked once per
-- (security, instant) that missed a close, so on a ledger with dense feed data it
-- is asked nothing.
trade_at AS (
    SELECT n.security_id, n.at AS ask_at, t.price AS ff_price, t.traded_at AS ff_at,
           t.currency_code AS ff_currency
    FROM (SELECT DISTINCT nt.security_id, nt.at FROM needs_trade nt) n
    CROSS JOIN LATERAL fn_last_trade_price(p_ledger_id, n.security_id, n.at) t
),
-- (4) Never traded at a price either: the position's FIFO cost basis at the
-- instant, the convention every current value carries such a position at.
-- MATERIALIZED so the pairs are settled before any walk runs: the walk is per
-- position in plpgsql, and only these pairs need it.
needs_cost AS MATERIALIZED (
    SELECT n.at, n.account_id, n.security_id
    FROM needs_trade n
    LEFT JOIN trade_at t ON t.security_id = n.security_id AND t.ask_at = n.at
    WHERE t.ff_price IS NULL
),
cost_at AS (
    SELECT n.at, n.account_id, n.security_id, ROUND(w.o_cost_basis, 4) AS cost_basis
    FROM needs_cost n
    CROSS JOIN LATERAL holdings_fifo_walk(n.account_id, n.security_id, n.at) w
),
resolved AS (
    SELECT h.at,
           h.account_id,
           h.security_id,
           h.quantity,
           COALESCE(f.ff_price, t.ff_price) AS obs_price,
           -- A same-day split is already reflected in that day's close, so a feed
           -- observation's boundary is the start of the following day.
           CASE WHEN f.ff_price IS NOT NULL
                THEN (f.ff_date + 1)::timestamptz
                ELSE t.ff_at
           END AS obs_at,
           c.cost_basis,
           CASE WHEN f.ff_price IS NOT NULL THEN 'feed'
                WHEN t.ff_price IS NOT NULL THEN 'trade'
                ELSE 'none'
           END AS priced_from,
           -- The currency of the price used; NULL for a position carried at cost basis,
           -- which is in its account's currency.
           CASE WHEN f.ff_price IS NOT NULL THEN f.ff_currency
                WHEN t.ff_price IS NOT NULL THEN t.ff_currency
           END AS price_currency
    FROM holdings_at h
    LEFT JOIN feed_at f
           ON f.security_id = h.security_id AND f.ask_at = h.at
    LEFT JOIN trade_at t
           ON t.security_id = h.security_id AND t.ask_at = h.at
    LEFT JOIN cost_at c
           ON c.account_id  = h.account_id
          AND c.security_id = h.security_id
          AND c.at          = h.at
)
-- Back-adjust the observed per-share price onto the instant's split basis: a price
-- seen before a split that has since happened is on the pre-split basis, and a
-- split-adjusted quantity times a raw price would count the split twice. ROUND
-- bounds the NUMERIC scale — unbounded division overflows System.Decimal. A
-- position with no observation is carried at its cost basis, which a split leaves
-- unchanged.
SELECT r.at AS as_of,
       r.account_id,
       r.security_id,
       ROUND(r.quantity, 12) AS quantity,
       CASE
           WHEN r.obs_price IS NULL THEN COALESCE(r.cost_basis, 0)
           ELSE ROUND(r.quantity * (
               SELECT CASE
                   WHEN f.factor > 0 AND f.factor <> 1
                   THEN ROUND(r.obs_price / f.factor, 12)
                   ELSE r.obs_price
               END
               FROM (
                   SELECT numeric_product(
                       CASE WHEN sa.ratios IS NULL THEN NULL::NUMERIC[]
                       ELSE ARRAY(
                           SELECT rr FROM unnest(sa.ratios, sa.split_ats) AS u(rr, sat)
                           WHERE sat > r.obs_at AND sat <= r.at)
                       END) AS factor
                   FROM (SELECT 1) one
                   LEFT JOIN split_arr sa ON sa.security_id = r.security_id
               ) f), 4)
       END AS market_value,
       r.priced_from,
       r.price_currency
FROM resolved r;
$function$;

GRANT EXECUTE ON FUNCTION holdings_market_value_as_of_set(uuid, timestamp with time zone[], uuid[])
    TO coffer_app, coffer_service;

COMMENT ON FUNCTION holdings_market_value_as_of_set(uuid, timestamp with time zone[], uuid[]) IS
    'Split-adjusted holdings market value for MANY instants in one pass (mig 200). '
    'Per (instant, position): quantity times the latest close on or before the '
    'instant''s date, else fn_last_trade_price at the instant (mig 259), back-adjusted '
    'onto the instant''s split basis; with neither, the position''s FIFO cost basis '
    '(priced_from ''none''). price_currency (mig 281) is the currency of the price used, '
    'NULL for a position carried at cost basis. Merges the requested instants into the '
    'leg and feed-price streams so each is one sort plus one window pass. Distinct '
    'name, not an overload: EF Core cannot map two functions to one SQL name.';

COMMIT;
