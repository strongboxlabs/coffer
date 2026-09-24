-- 229: holdings date reads follow the ADR-0003 override layer.
--
-- WHAT WAS WRONG. The register renders COALESCE(o.posted_at, h.posted_at) --
-- the user's curated date wins. Every holdings function read the RAW
-- txn_headers.posted_at instead. So a row could sit in one place on screen and
-- in another in the FIFO walk that decides which lots a sale consumes, which
-- cost basis it books and which reporting period the gain lands in.
--
-- The gap was already live, not hypothetical: an investment merge stamps a
-- posted_at override on the SURVIVOR (it adopts the folded row's date), so
-- every merge winner already had a displayed date its cost basis did not know
-- about.
--
-- WHY ALL THREE FUNCTIONS. holdings_fifo_walk is the one with the bug, but
-- moving it alone would make the as-of feeders disagree with it: a position
-- could be inside the cutoff for cost basis and outside it for market value at
-- the same instant. Date semantics have to be uniform or the reports contradict
-- each other, so holdings_cost_basis_as_of and holdings_market_value_as_of_set
-- move in the same migration.
--
-- NOT CHANGED: is_hidden. These functions read it raw, and that is correct --
-- txn_header_overrides.is_hidden is only ever carried forward by the override
-- upsert, never set; hiding writes txn_headers.is_hidden directly. If that ever
-- changes, these predicates need the same treatment.
--
-- NOT resolved_transactions. Tempting, and wrong: its amount is
-- COALESCE(lo.amount, l.amount), which would fold LEG overrides into cost
-- basis, and it COALESCEs security_id and quantity across a posting's sibling
-- legs, which would double-count every holdings leg against its cash twin.
--
-- These bodies were extracted from the LIVE catalogue with pg_get_functiondef
-- and edited in place -- NOT copied forward from an older migration. Migration
-- 227 records why: copying 163 forward silently reverted a fix that 209 had to
-- undo.

-- 1 of 3: the FIFO walk -- cost basis, lots, realized gains.
CREATE OR REPLACE FUNCTION public.holdings_fifo_walk(p_account_id uuid, p_security_id uuid, p_as_of timestamp with time zone DEFAULT NULL::timestamp with time zone, OUT o_quantity numeric, OUT o_cost_basis numeric, OUT o_lots holdings_fifo_lot[], OUT o_gains holdings_fifo_gain[])
 RETURNS record
 LANGUAGE plpgsql
 STABLE PARALLEL SAFE
AS $function$
DECLARE
    v_include_fees BOOLEAN;
    v_event        RECORD;
    v_fee          NUMERIC;
    v_remaining    NUMERIC;
    v_consumed     NUMERIC;
    v_take         NUMERIC;
    v_proceeds     NUMERIC;
    v_i            INT;
    v_lot          holdings_fifo_lot;
    -- ADR-0064 ST/LT split (mig 169), accumulated per sell.
    v_consumed_lt  NUMERIC;
    v_qty_lt       NUMERIC;
    v_consumed_qty NUMERIC;
    v_proceeds_lt  NUMERIC;
BEGIN
    o_quantity   := 0;
    o_cost_basis := 0;
    o_lots       := ARRAY[]::holdings_fifo_lot[];
    o_gains      := ARRAY[]::holdings_fifo_gain[];

    -- Does this brokerage fold trade commissions into basis (mig 056)?
    SELECT COALESCE(b.is_trade_commission, FALSE)
    INTO v_include_fees
    FROM accounts b
    WHERE b.holdings_account_id = p_account_id;
    v_include_fees := COALESCE(v_include_fees, FALSE);

    FOR v_event IN
        SELECT 'leg'::TEXT AS kind,
               COALESCE(o.posted_at, hd.posted_at) AS event_at,
               hd.action    AS action,
               l.id         AS leg_id,
               l.header_id,
               l.amount,
               l.quantity,
               NULL::NUMERIC AS ratio,
               CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
        FROM txn_legs l
        JOIN live_txn_headers hd ON hd.id = l.header_id   -- excludes recurring templates (mig 124)
        -- mig 229: the EFFECTIVE date, not the raw one. See the migration header.
        LEFT JOIN txn_header_overrides o ON o.header_id = hd.id
        WHERE l.security_id = p_security_id
          AND l.account_id  = p_account_id
          AND l.quantity   IS NOT NULL
          AND hd.is_hidden  = FALSE
          AND hd.is_merged_into IS NULL                   -- mig 163
          AND (p_as_of IS NULL OR COALESCE(o.posted_at, hd.posted_at) <= p_as_of)

        UNION ALL

        SELECT 'split'::TEXT, ss.split_at, NULL::TEXT, NULL::UUID, NULL::UUID,
               NULL::NUMERIC, NULL::NUMERIC, ss.ratio, 0
        FROM security_splits ss
        WHERE ss.security_id = p_security_id
          AND (p_as_of IS NULL OR ss.split_at <= p_as_of)

        ORDER BY event_at, sort_class, leg_id
    LOOP
        IF v_event.kind = 'split' THEN
            o_quantity := o_quantity * v_event.ratio;
            -- Quantity scales up, unit_cost down, so lot COST is unchanged — required
            -- where basis = Σ open-lot cost (ADR-0064).
            FOR v_i IN 1 .. COALESCE(array_length(o_lots, 1), 0) LOOP
                IF NOT o_lots[v_i].is_closed THEN
                    o_lots[v_i].quantity := o_lots[v_i].quantity * v_event.ratio;
                    IF v_event.ratio <> 0 THEN
                        o_lots[v_i].unit_cost := o_lots[v_i].unit_cost / v_event.ratio;
                    END IF;
                END IF;
            END LOOP;

        ELSIF v_event.quantity > 0 THEN
            -- Buy / reinvest: one new lot, cost including the fee when folded.
            IF v_include_fees THEN
                v_fee := COALESCE((
                    SELECT SUM(fl.amount) FROM txn_legs fl
                    WHERE fl.header_id = v_event.header_id
                      AND fl.posting_role = 'fee' AND fl.amount > 0), 0);
            ELSE
                v_fee := 0;
            END IF;

            v_lot.leg_id      := v_event.leg_id;
            v_lot.quantity    := v_event.quantity;
            v_lot.unit_cost   := CASE WHEN v_event.quantity = 0 THEN 0
                                      ELSE (v_event.amount + v_fee) / v_event.quantity END;
            v_lot.acquired_at := v_event.event_at;
            v_lot.is_closed   := FALSE;
            o_lots := o_lots || v_lot;

            o_quantity   := o_quantity + v_event.quantity;
            o_cost_basis := o_cost_basis + v_event.amount + v_fee;

        ELSIF v_event.quantity < 0 THEN
            -- Sell: consume lots FIFO. The array is appended in event order, which is
            -- (acquired_at, leg_id) — a stable ordering, unlike mig 148's
            -- ORDER BY acquired_at, <random lot uuid>.
            v_remaining   := ABS(v_event.quantity);
            v_consumed    := 0;
            v_consumed_lt := 0;
            v_qty_lt      := 0;
            FOR v_i IN 1 .. COALESCE(array_length(o_lots, 1), 0) LOOP
                EXIT WHEN v_remaining <= 0;
                CONTINUE WHEN o_lots[v_i].is_closed OR o_lots[v_i].quantity <= 0;

                IF o_lots[v_i].quantity <= v_remaining THEN
                    v_take := o_lots[v_i].quantity;
                    o_lots[v_i].quantity  := 0;
                    o_lots[v_i].is_closed := TRUE;
                ELSE
                    v_take := v_remaining;
                    o_lots[v_i].quantity := o_lots[v_i].quantity - v_remaining;
                END IF;
                v_consumed := v_consumed + v_take * COALESCE(o_lots[v_i].unit_cost, 0);

                -- Long-term iff the sale is more than a year after acquisition.
                -- Splits preserve acquired_at, so the clock runs from the first buy.
                IF v_event.event_at > o_lots[v_i].acquired_at + INTERVAL '1 year' THEN
                    v_consumed_lt := v_consumed_lt + v_take * COALESCE(o_lots[v_i].unit_cost, 0);
                    v_qty_lt      := v_qty_lt + v_take;
                END IF;

                v_remaining := v_remaining - v_take;
            END LOOP;

            o_cost_basis := o_cost_basis - v_consumed;
            o_quantity   := o_quantity + v_event.quantity;

            -- A transfer_shares disposal consumes lots but is NOT a sale, so it
            -- records no realized gain (ADR-0065 D1).
            IF v_event.action IS DISTINCT FROM 'transfer_shares' THEN
                IF v_include_fees THEN
                    v_fee := COALESCE((
                        SELECT SUM(fl.amount) FROM txn_legs fl
                        WHERE fl.header_id = v_event.header_id
                          AND fl.posting_role = 'fee' AND fl.amount > 0), 0);
                ELSE
                    v_fee := 0;
                END IF;
                v_proceeds := (-v_event.amount) - v_fee;

                -- Apportion proceeds to the long-term bucket by consumed-share share.
                -- Multiply before dividing so an exactly divisible split stays exact
                -- (4500 * 10 / 15 = 3000, not 4500 * 0.666... rounded).
                v_consumed_qty := ABS(v_event.quantity) - v_remaining;
                IF v_consumed_qty > 0 THEN
                    v_proceeds_lt := v_proceeds * v_qty_lt / v_consumed_qty;
                ELSE
                    v_proceeds_lt := 0;
                END IF;

                o_gains := o_gains || ROW(
                    v_event.leg_id, v_event.event_at, ABS(v_event.quantity),
                    v_proceeds, v_consumed, v_proceeds - v_consumed,
                    v_proceeds_lt, v_consumed_lt,
                    v_proceeds_lt - v_consumed_lt)::holdings_fifo_gain;
            END IF;
        END IF;
    END LOOP;
END;
$function$;

-- 2 of 3: as-of cost basis (position discovery + cutoff).
CREATE OR REPLACE FUNCTION public.holdings_cost_basis_as_of(p_ledger_id uuid, p_as_of timestamp with time zone, p_account_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(account_id uuid, security_id uuid, quantity numeric, cost_basis numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    -- Positions are discovered from the LEGS, not from the holdings projection: a
    -- position closed since p_as_of has no projection row but was held then. The
    -- qty <> 0 filter drops anything closed BY p_as_of, matching the valuation feeder.
    SELECT p.account_id,
           p.security_id,
           ROUND(w.o_quantity, 12)  AS quantity,
           ROUND(w.o_cost_basis, 4) AS cost_basis
    FROM (
        SELECT DISTINCT l.account_id, l.security_id
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        -- mig 229: effective date, matching holdings_fifo_walk.
        LEFT JOIN txn_header_overrides o ON o.header_id = h.id
        WHERE l.ledger_id     = p_ledger_id
          AND l.security_id  IS NOT NULL
          AND l.quantity     IS NOT NULL
          AND h.is_hidden     = FALSE
          AND COALESCE(o.posted_at, h.posted_at) <= p_as_of
          AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
    ) p
    CROSS JOIN LATERAL holdings_fifo_walk(p.account_id, p.security_id, p_as_of) w
    WHERE w.o_quantity <> 0;
$function$;

-- 3 of 3: as-of market value (net-worth history).
CREATE OR REPLACE FUNCTION public.holdings_market_value_as_of_set(p_ledger_id uuid, p_as_ofs timestamp with time zone[], p_account_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(as_of timestamp with time zone, account_id uuid, security_id uuid, quantity numeric, market_value numeric, priced_from text)
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
           COALESCE(o_h.posted_at, h.posted_at) AS posted_at,
           l.quantity,
           CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
    FROM txn_legs l
    JOIN live_txn_headers h ON h.id = l.header_id
    -- mig 229: effective date, matching holdings_fifo_walk.
    LEFT JOIN txn_header_overrides o_h ON o_h.header_id = h.id
    CROSS JOIN horizon
    WHERE l.ledger_id      = p_ledger_id
      AND l.security_id   IS NOT NULL
      AND l.quantity      IS NOT NULL
      AND COALESCE(o_h.posted_at, h.posted_at)     <= horizon.t_max
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
    SELECT l.account_id, l.security_id, COALESCE(o_hh.posted_at, hh.posted_at) AS at, 0 AS is_ask,
           l.unit_price, l.id AS leg_id, NULL::TIMESTAMPTZ AS ask_at
    FROM txn_legs l
    JOIN live_txn_headers hh ON hh.id = l.header_id
    -- mig 229: effective date, matching holdings_fifo_walk.
    LEFT JOIN txn_header_overrides o_hh ON o_hh.header_id = hh.id
    CROSS JOIN horizon
    WHERE l.ledger_id      = p_ledger_id
      AND l.security_id   IS NOT NULL
      AND l.unit_price    IS NOT NULL
      AND COALESCE(o_hh.posted_at, hh.posted_at)    <= horizon.t_max
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
$function$;


-- Existing lots and realized_gains were derived under the raw ordering, and the
-- consistency checker cannot flag the difference -- it compares stored state
-- against this same walk, so both sides would move together and agree. Recompute
-- every ledger so the stored projection matches the new reading.
--
-- Cheap in practice: only rows whose effective date differs from their raw one
-- can re-sort at all, and the only writer of a posted_at override on an
-- investment row is a merge.
SELECT recompute_holdings_cost_basis(NULL, NULL, NULL);
