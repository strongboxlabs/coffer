-- 200 — batched as-of holdings valuation: every requested instant in ONE pass
--
-- WHY
-- ---
-- holdings_market_value_as_of (mig 172) values a single instant by replaying, for
-- every (holdings-account, security), that position's entire leg + split history
-- from the beginning, then probing for a price. Correct, and the shape is fine for
-- one instant — but a time-weighted return values the portfolio once per instant
-- money crossed the scope's boundary, and a real five-year ledger reaches ~420.
-- The whole ledger was therefore replayed 420 times, in plpgsql, row at a time.
--
-- That cost is the entire reason MaxReturnsBoundaries existed — a cap that refused
-- the headline whole-portfolio TWR outright rather than compute it. This function
-- removes the cause so the cap can be deleted rather than retuned again (it was
-- set from a bad measurement three times).
--
-- HOW
-- ---
-- One idea applied three times: MERGE THE REQUESTED INSTANTS INTO THE DATA STREAM
-- AS PSEUDO-ROWS, sort once, and let a window function carry the answer to them.
-- Nothing is probed per (position, instant); every stage is a sort plus a pass.
--
-- 1. QUANTITY. Splits are folded into the legs instead of replayed as events. A
--    leg is restated into the split basis of the LAST requested instant:
--
--        A_l = qty_l x PROD{ r_s : split_at_s > posted_at_l, split_at_s <= Tmax }
--
--    STRICTLY after is what reproduces mig 172's canonical order, where a split
--    sorts before the legs sharing its instant and so does not scale them. In that
--    common basis the running quantity is a plain prefix SUM — one window pass —
--    and reading it at an instant T divides out the splits not yet due by T:
--
--        qty(T) = SUM{ A_l : l <= T } / PROD{ r_s : split_at_s > T, <= Tmax }
--
--    Every term of that sum contains the divisor as a factor, so it cancels
--    exactly. This is mig 172's product rearranged, not an approximation.
--
-- 2. FEED PRICE. The latest close on or before each instant, by forward fill: the
--    price rows and the instants go into one stream, count() over the ordering
--    numbers the islands between prices, and first_value() inside an island hands
--    each instant the price that opened it. Date granularity, matching mig 172's
--    `price_date <= p_as_of::date` — deliberately not a timestamp comparison, so
--    the two cannot diverge on a session TimeZone.
--
-- 3. TRADE PRICE. The same fill over trade legs per (account, security), used only
--    where no feed close exists. Each price row opens its own island, so an
--    instant sharing a timestamp with several trades takes the last of them —
--    mig 172's `ORDER BY posted_at DESC, id DESC LIMIT 1`.
--
-- WHAT THIS COST TO GET RIGHT
-- ---------------------------
-- The first version kept prices as per-row probes and needed TWO of them per row
-- (price, then its timestamp). On a ledger with no feed closes at all — every row
-- falling to tier 2 — that made the batched function SLOWER than the per-instant
-- one it replaced: 11.6 s against 6.6 s for 420 instants. Collapsing to one
-- LATERAL got it to 3.0 s; forward-filling both tiers is what removes the probes
-- altogether. The lesson is the one this follow-up already carried: measure the
-- thing, do not reason about it.
--
-- EQUIVALENCE
-- -----------
-- mig 172 is left in place, unchanged, and is the reference — net_worth_history,
-- allocation and holdings_snapshot still call it. The batched form is a DISTINCT
-- name, not an overload: EF Core cannot map two functions to one SQL name (it
-- fails inside CreateColumnExpression), which is why account_balance_as_of_set is
-- named as it is. A test suite asserts the two agree row for row over splits,
-- fractional and reverse ratios, a split sharing an instant with a trade, a
-- position closed mid-window, a price observed across a split, trade-only
-- pricing, and one security held in several accounts.

-- ---------------------------------------------------------------------------
-- Exact product of a NUMERIC array. Postgres has no product aggregate, and
-- exp(sum(ln(x))) is float: split ratios must multiply EXACTLY or a 3-for-1 turns
-- 100 shares into 299.999999999999.
--
-- SQL over a custom aggregate rather than a plpgsql loop, because this is
-- evaluated once per leg and once per (position, instant) — hundreds of thousands
-- of calls at stress scale, where a non-inlinable body is measurable.
-- ---------------------------------------------------------------------------
CREATE FUNCTION numeric_mul(a NUMERIC, b NUMERIC)
RETURNS NUMERIC
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$ SELECT a * b $$;

CREATE AGGREGATE numeric_product_agg(NUMERIC) (
    SFUNC    = numeric_mul,
    STYPE    = NUMERIC,
    INITCOND = '1'
);

CREATE FUNCTION numeric_product(p_values NUMERIC[])
RETURNS NUMERIC
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
    SELECT COALESCE(
        (SELECT numeric_product_agg(x) FROM unnest(p_values) AS t(x) WHERE x IS NOT NULL),
        1::NUMERIC);
$$;

COMMENT ON FUNCTION numeric_product(NUMERIC[]) IS
    'Exact product of a NUMERIC array (1 for empty/NULL). Split-ratio folding needs '
    'exact multiplication; exp(sum(ln())) is float and drifts.';

GRANT EXECUTE ON FUNCTION numeric_product(NUMERIC[]) TO coffer_app;

-- ---------------------------------------------------------------------------
-- Batched split-adjusted holdings market value.
--
-- p_account_ids: NULL for every holdings account in the ledger, a set to restrict.
-- An EMPTY array means NO accounts and returns nothing — the two are not
-- interchangeable, and reading empty as "all" would value a whole ledger for a
-- caller that asked for none.
-- ---------------------------------------------------------------------------
CREATE FUNCTION holdings_market_value_as_of_set(
    p_ledger_id   UUID,
    p_as_ofs      TIMESTAMPTZ[],
    p_account_ids UUID[] DEFAULT NULL
)
RETURNS TABLE(
    as_of        TIMESTAMPTZ,
    account_id   UUID,
    security_id  UUID,
    quantity     NUMERIC,
    market_value NUMERIC,
    priced_from  TEXT
)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
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
           h.posted_at,
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
-- (2) Feed price by forward fill, at DATE granularity to match mig 172.
ask_dates AS (
    SELECT at, at::date AS on_date FROM asks
),
feed_stream AS (
    SELECT sp.security_id, sp.price_date AS on_date, 0 AS is_ask,
           sp.price, NULL::TIMESTAMPTZ AS ask_at
    FROM security_prices sp
    CROSS JOIN horizon
    WHERE sp.ledger_id   = p_ledger_id
      AND sp.price_date <= horizon.t_max::date
      AND sp.security_id IN (SELECT security_id FROM held_securities)
    UNION ALL
    SELECT hs.security_id, ad.on_date, 1, NULL::NUMERIC, ad.at
    FROM held_securities hs
    CROSS JOIN ask_dates ad
),
feed_islands AS (
    SELECT security_id, on_date, is_ask, price, ask_at,
           COUNT(price) OVER (
               PARTITION BY security_id ORDER BY on_date, is_ask
           ) AS island
    FROM feed_stream
),
feed_at AS (
    SELECT security_id, ask_at, ff_price, ff_date
    FROM (
        SELECT security_id, ask_at,
               FIRST_VALUE(price)   OVER w AS ff_price,
               FIRST_VALUE(on_date) OVER w AS ff_date
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
-- (3) Trade price by the same fill, per (account, security). Each price row opens
-- its own island, so an instant sharing a timestamp with several trades takes the
-- last of them — mig 172's ORDER BY posted_at DESC, id DESC.
trade_stream AS (
    SELECT l.account_id, l.security_id, hh.posted_at AS at, 0 AS is_ask,
           l.unit_price, l.id AS leg_id, NULL::TIMESTAMPTZ AS ask_at
    FROM txn_legs l
    JOIN live_txn_headers hh ON hh.id = l.header_id
    CROSS JOIN horizon
    WHERE l.ledger_id      = p_ledger_id
      AND l.security_id   IS NOT NULL
      AND l.unit_price    IS NOT NULL
      AND hh.posted_at    <= horizon.t_max
      AND hh.is_hidden     = FALSE
      AND hh.is_merged_into IS NULL
      AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
    UNION ALL
    -- Only the (position, instant) pairs that MISSED a feed close ask for a trade
    -- price. On a ledger with dense feed data that is nothing, so the whole stream
    -- collapses to the price rows and the sort is trivial; without the gate this
    -- built and sorted a full positions x instants cross join to answer nobody.
    SELECT n.account_id, n.security_id, n.at, 1, NULL::NUMERIC, NULL::UUID, n.at
    FROM needs_trade n
),
trade_islands AS (
    SELECT account_id, security_id, at, is_ask, unit_price, leg_id, ask_at,
           COUNT(unit_price) OVER (
               PARTITION BY account_id, security_id ORDER BY at, is_ask, leg_id NULLS FIRST
           ) AS island
    FROM trade_stream
),
trade_at AS (
    SELECT account_id, security_id, ask_at, ff_price, ff_at
    FROM (
        SELECT account_id, security_id, ask_at,
               FIRST_VALUE(unit_price) OVER w AS ff_price,
               FIRST_VALUE(at)         OVER w AS ff_at
        FROM trade_islands
        WINDOW w AS (
            PARTITION BY account_id, security_id, island
            ORDER BY at, is_ask, leg_id NULLS FIRST
        )
    ) x
    WHERE x.ask_at IS NOT NULL
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
           CASE WHEN f.ff_price IS NOT NULL THEN 'feed'
                WHEN t.ff_price IS NOT NULL THEN 'trade'
                ELSE 'none'
           END AS priced_from
    FROM holdings_at h
    LEFT JOIN feed_at f
           ON f.security_id = h.security_id AND f.ask_at = h.at
    LEFT JOIN trade_at t
           ON t.account_id  = h.account_id
          AND t.security_id  = h.security_id
          AND t.ask_at       = h.at
)
-- Back-adjust the observed per-share price onto the instant's split basis: a price
-- seen before a split that has since happened is on the pre-split basis, and a
-- split-adjusted quantity times a raw price would count the split twice. ROUND
-- bounds the NUMERIC scale — unbounded division overflows System.Decimal.
SELECT r.at AS as_of,
       r.account_id,
       r.security_id,
       ROUND(r.quantity, 12) AS quantity,
       ROUND(r.quantity * COALESCE(
           CASE
               WHEN r.obs_price IS NULL OR r.obs_at IS NULL THEN r.obs_price
               ELSE (
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
                   ) f
               )
           END, 0), 4) AS market_value,
       r.priced_from
FROM resolved r;
$$;

COMMENT ON FUNCTION holdings_market_value_as_of_set(UUID, TIMESTAMPTZ[], UUID[]) IS
    'Split-adjusted holdings market value for MANY instants in one pass (mig 200). '
    'Row-for-row equivalent to calling holdings_market_value_as_of once per instant, '
    'but merges the requested instants into the leg, feed-price and trade-price '
    'streams so each is one sort plus one window pass, with nothing probed per '
    '(position, instant). Distinct name, not an overload: EF Core cannot map two '
    'functions to one SQL name.';

GRANT EXECUTE ON FUNCTION holdings_market_value_as_of_set(UUID, TIMESTAMPTZ[], UUID[]) TO coffer_app;
