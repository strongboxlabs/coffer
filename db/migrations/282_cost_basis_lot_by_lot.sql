-- =============================================================================
-- 282 — a cost basis can be read lot by lot, at each lot's acquisition date
-- =============================================================================
--
-- WHY
-- ---
-- A trade converts at its trade date's rate, so a realized gain includes the FX move
-- (ADR-0114 D7). A cost basis is the cost of the lots that make it up, each bought on
-- its own day: converted as a whole at one day's rate it would be a figure no trade
-- happened at, and a combined realized gain would leave out the move between buying
-- and selling. Within one account nothing converts: every lot of a brokerage is in the
-- brokerage's currency. Only a figure that combines accounts in different currencies
-- needs the lots, and it needs their acquisition dates, which no read returned.
--
-- WHAT CHANGES
-- ------------
--   * holdings_fifo_gain_lot (new type): one take of a disposal from one lot, the
--     sale's leg, the lot's acquisition instant, the shares and their cost.
--   * holdings_fifo_walk gains OUT o_gain_lots: for every disposal that is a sale
--     (the ones o_gains records), each lot it consumed. The body is migration 256's,
--     as 257 re-issued it, with these asserted edits only: v_takes declared, it and
--     o_gain_lots initialised, each take recorded beside v_consumed, and a sale's
--     takes appended where its gain is. Nothing
--     the walk computed before changes; Σ cost of a sale's lots is its v_consumed.
--     The OUT shape changes, so it is dropped and created again. Its callers
--     (recompute_holdings_cost_basis, plpgsql, SELECT * INTO a record; the
--     string-bodied SQL readers) hold no dependency on it and read the columns they
--     read before.
--   * holdings_open_lots_as_of(ledger, as_of, accounts) (new): every lot open at an
--     instant on the given accounts, with its acquisition instant and cost, in one
--     statement. Positions are discovered as holdings_cost_basis_as_of discovers them,
--     so the lots of a position sum to its cost basis there.
--   * realized_gain_lots(ledger, accounts) (new): every sale's lots on the given
--     accounts, from the unbounded walk the realized_gains writer persists, so a sale's
--     lots sum to its stored cost basis sold, before its rounding.
--
-- Both readers take the accounts to answer for: only an account whose currency
-- differs from the one its figure is shown in needs its lots, and every other reader
-- uses the stored or as-of totals as before.
--
-- No data is read or written.
-- =============================================================================

BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'holdings_fifo_gain_lot') THEN
        CREATE TYPE holdings_fifo_gain_lot AS (
            sell_leg_id uuid,
            acquired_at timestamp with time zone,
            quantity    numeric,
            cost        numeric
        );
    END IF;
END
$$;

COMMENT ON TYPE holdings_fifo_gain_lot IS
    'Mig 282. One take of a sale from one FIFO lot: the sale''s leg, the lot''s '
    'acquisition instant, the shares taken and their cost (shares times the lot''s '
    'unit cost). A sale''s takes sum to its cost basis sold.';

DROP FUNCTION IF EXISTS holdings_fifo_walk(uuid, uuid, timestamp with time zone, uuid);

CREATE FUNCTION public.holdings_fifo_walk(p_account_id uuid, p_security_id uuid, p_as_of timestamp with time zone DEFAULT NULL::timestamp with time zone, p_exclude_header_id uuid DEFAULT NULL::uuid, OUT o_quantity numeric, OUT o_cost_basis numeric, OUT o_lots holdings_fifo_lot[], OUT o_gains holdings_fifo_gain[], OUT o_shortfall numeric, OUT o_gain_lots holdings_fifo_gain_lot[])
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
    -- mig 282: the lots one disposal consumed, kept only when it is a sale.
    v_takes        holdings_fifo_gain_lot[];
BEGIN
    o_quantity   := 0;
    o_cost_basis := 0;
    o_lots       := ARRAY[]::holdings_fifo_lot[];
    o_gains      := ARRAY[]::holdings_fifo_gain[];
    o_shortfall  := 0;
    o_gain_lots  := ARRAY[]::holdings_fifo_gain_lot[];

    -- Does this brokerage fold trade commissions into basis (mig 056)?
    SELECT COALESCE(b.is_trade_commission, FALSE)
    INTO v_include_fees
    FROM accounts b
    WHERE b.holdings_account_id = p_account_id;
    v_include_fees := COALESCE(v_include_fees, FALSE);

    FOR v_event IN
        SELECT 'leg'::TEXT AS kind,
               hd.posted_at AS event_at,
               hd.action    AS action,
               l.id         AS leg_id,
               l.header_id,
               l.amount,
               l.quantity,
               NULL::NUMERIC AS ratio,
               CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
        FROM txn_legs l
        JOIN live_txn_headers hd ON hd.id = l.header_id   -- excludes recurring templates (mig 124)
        WHERE l.security_id = p_security_id
          AND l.account_id  = p_account_id
          AND l.quantity   IS NOT NULL
          AND hd.is_hidden  = FALSE
          AND hd.is_merged_into IS NULL                   -- mig 163
          AND (p_as_of IS NULL OR hd.posted_at <= p_as_of)
          AND (p_exclude_header_id IS NULL OR l.header_id <> p_exclude_header_id)  -- mig 256

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
            v_takes       := ARRAY[]::holdings_fifo_gain_lot[];
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
                -- mig 282: which lot this take came from, and what it cost, so a cost
                -- sold can be read at each lot's acquisition date (ADR-0114 D7).
                v_takes := v_takes || ROW(
                    v_event.leg_id, o_lots[v_i].acquired_at, v_take,
                    v_take * COALESCE(o_lots[v_i].unit_cost, 0))::holdings_fifo_gain_lot;

                -- Long-term iff the sale is more than a year after acquisition.
                -- Splits preserve acquired_at, so the clock runs from the first buy.
                IF v_event.event_at > o_lots[v_i].acquired_at + INTERVAL '1 year' THEN
                    v_consumed_lt := v_consumed_lt + v_take * COALESCE(o_lots[v_i].unit_cost, 0);
                    v_qty_lt      := v_qty_lt + v_take;
                END IF;

                v_remaining := v_remaining - v_take;
            END LOOP;

            -- mig 256: what this disposal could not match. It was dropped here,
            -- silently: o_quantity below still takes the FULL sale, so the
            -- holding goes short, and the gain row books the unmatched shares
            -- at zero cost. Reported so a writer can refuse to make it worse.
            o_shortfall  := o_shortfall + GREATEST(v_remaining, 0);
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
                -- (v_proceeds * v_qty_lt / v_consumed_qty is exact whenever the
                -- product divides evenly; taking the share ratio first would round
                -- a repeating fraction such as two-thirds before multiplying).
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
                -- mig 282: the gain's lots, beside the gain they make up.
                o_gain_lots := o_gain_lots || v_takes;
            END IF;
        END IF;
    END LOOP;
END;
$function$;

GRANT EXECUTE ON FUNCTION holdings_fifo_walk(uuid, uuid, timestamp with time zone, uuid)
    TO coffer_app;

COMMENT ON FUNCTION holdings_fifo_walk(uuid, uuid, timestamp with time zone, uuid) IS
    'ADR-0064 FIFO walk, pure (mig 202). The ONE implementation of the FIFO rules; '
    'recompute_holdings_cost_basis persists it, the *_as_of readers ask it about an '
    'instant. Mig 256: o_shortfall = shares disposals could not match; '
    'p_exclude_header_id leaves one header out of the stream. Mig 282: o_gain_lots = '
    'each lot every sale consumed, with its acquisition instant and cost.';

-- ---------------------------------------------------------------------------
-- Every lot open at an instant, on the given accounts.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.holdings_open_lots_as_of(p_ledger_id uuid, p_as_of timestamp with time zone, p_account_ids uuid[])
 RETURNS TABLE(account_id uuid, security_id uuid, acquired_at timestamp with time zone, quantity numeric, cost numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    -- Positions discovered from the legs, as holdings_cost_basis_as_of discovers them,
    -- so a position's open lots sum to the cost basis it reports at the instant.
    SELECT p.account_id,
           p.security_id,
           l.acquired_at,
           ROUND(l.quantity, 12)               AS quantity,
           ROUND(l.quantity * l.unit_cost, 12) AS cost
    FROM (
        SELECT DISTINCT tl.account_id, tl.security_id
        FROM txn_legs tl
        JOIN live_txn_headers h ON h.id = tl.header_id
        WHERE tl.ledger_id     = p_ledger_id
          AND tl.security_id  IS NOT NULL
          AND tl.quantity     IS NOT NULL
          AND h.is_hidden      = FALSE
          AND h.posted_at     <= p_as_of
          AND tl.account_id    = ANY (p_account_ids)
    ) p
    CROSS JOIN LATERAL holdings_fifo_walk(p.account_id, p.security_id, p_as_of) w
    CROSS JOIN LATERAL unnest(w.o_lots) l
    WHERE NOT l.is_closed
      AND l.quantity > 0;
$function$;

GRANT EXECUTE ON FUNCTION holdings_open_lots_as_of(uuid, timestamp with time zone, uuid[])
    TO coffer_app, coffer_service;

COMMENT ON FUNCTION holdings_open_lots_as_of(uuid, timestamp with time zone, uuid[]) IS
    'Mig 282. Every FIFO lot open at p_as_of on the given accounts, with its acquisition '
    'instant and cost (shares times unit cost): what a cost basis converts lot by lot '
    'at each acquisition date (ADR-0114 D7). A position''s lots sum to its '
    'holdings_cost_basis_as_of cost basis.';

-- ---------------------------------------------------------------------------
-- Every sale's lots, on the given accounts.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.realized_gain_lots(p_ledger_id uuid, p_account_ids uuid[])
 RETURNS TABLE(account_id uuid, security_id uuid, sell_leg_id uuid, acquired_at timestamp with time zone, quantity numeric, cost numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    -- The unbounded walk, the one recompute_holdings_cost_basis persists into
    -- realized_gains, over every position the accounts ever moved shares on.
    SELECT p.account_id,
           p.security_id,
           g.sell_leg_id,
           g.acquired_at,
           ROUND(g.quantity, 12) AS quantity,
           ROUND(g.cost, 12)     AS cost
    FROM (
        SELECT DISTINCT tl.account_id, tl.security_id
        FROM txn_legs tl
        WHERE tl.ledger_id    = p_ledger_id
          AND tl.security_id IS NOT NULL
          AND tl.quantity    IS NOT NULL
          AND tl.account_id   = ANY (p_account_ids)
    ) p
    CROSS JOIN LATERAL holdings_fifo_walk(p.account_id, p.security_id, NULL) w
    CROSS JOIN LATERAL unnest(w.o_gain_lots) g;
$function$;

GRANT EXECUTE ON FUNCTION realized_gain_lots(uuid, uuid[])
    TO coffer_app, coffer_service;

COMMENT ON FUNCTION realized_gain_lots(uuid, uuid[]) IS
    'Mig 282. Every sale''s FIFO lots on the given accounts, from the unbounded walk '
    'recompute_holdings_cost_basis persists: the sale''s leg, each lot''s acquisition '
    'instant, shares and cost. A sale''s lots sum to its cost basis sold before the '
    'writer rounds it. What a realized gain''s cost converts at, lot by lot (ADR-0114 D7).';

COMMIT;
