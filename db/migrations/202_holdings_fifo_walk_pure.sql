-- 202 — make the FIFO walk pure, so cost basis can be asked for a past instant
--
-- WHY
-- ---
-- `holdings_snapshot` gained an as-of parameter, but only quantity and market value
-- could follow it. Cost basis could not, because FIFO basis (ADR-0064) is produced
-- by recompute_holdings_cost_basis (mig 148) — and that function keeps its working
-- state IN THE `lots` TABLE. It resets lots to acquired state, scales them with
-- `UPDATE lots` on each split, and consumes them with `UPDATE lots` on each sell.
-- A read cannot borrow an algorithm whose state is a table it must not write.
--
-- The tempting answers were both bad. Duplicating the walk read-only would give two
-- implementations of the FIFO rules held together by a test rather than by
-- construction. Calling the writer inside a transaction that rolls back would make
-- a REPORT take row locks on holdings, lots and realized_gains and block concurrent
-- writers.
--
-- So the walk becomes PURE and gains an as-of bound. It computes in memory and
-- RETURNS what it derived; the writer persists that. One algorithm, two callers:
--
--   holdings_fifo_walk(account, security, as_of)  -- pure, STABLE
--       -> quantity, cost_basis, lots[], gains[]
--
--   recompute_holdings_cost_basis  -- calls it with as_of = NULL, persists
--   holdings_cost_basis_as_of      -- calls it with an instant, returns basis
--
-- WHAT MOVES, EXACTLY
-- -------------------
-- The event stream, the ordering (split before buy before sell on a shared instant),
-- fee folding for is_trade_commission brokerages, split handling (quantity x ratio,
-- unit_cost / ratio so lot COST is unchanged), FIFO consumption, basis reduction by
-- CONSUMED COST rather than average cost, and proceeds net of a sell-side fee — all
-- unchanged in behaviour and now expressed over arrays instead of table updates.
--
-- PORTED FROM MIGRATION 169, which is this function's current definition — not from
-- 148, which introduced FIFO but was superseded three times after it. A first draft
-- of this migration ported 148 and silently dropped everything added later: the
-- merged-header exclusion (163), the transfer_shares disposal gate (152/165), and
-- the realized-gains short/long-term split (169). The last surfaced as long-term
-- gain reading 0.00 in an existing test, because mig 169's three _lt columns are
-- NOT NULL DEFAULT 0 and an INSERT omitting them silently gets zeros. So every
-- behaviour that has to survive is listed here:
--
--   * merged and hidden headers excluded from the event stream and from lot reset;
--   * a transfer_shares disposal consumes lots but records NO realized_gains row
--     (ADR-0065 D1 - it is a transfer, not a sale);
--   * availability gate (ADR-0065 D3): a sale consumes only lots that have ARRIVED
--     by its instant. In memory that holds by construction - the lot array contains
--     only events already walked, and events are time-ordered - where the table
--     version needed an explicit join on the lot leg's header date;
--   * ST/LT split (mig 169): a consumed lot is long-term iff the sale is more than
--     one year after its acquired_at (splits preserve acquired_at), with proceeds
--     apportioned to the long-term bucket by consumed-share share, multiplying
--     before dividing so an exactly divisible split stays exact;
--   * fee folding for is_trade_commission brokerages, on both buys and sells.
--
-- ONE DELIBERATE BEHAVIOUR CHANGE, and it is a fix. The writer consumed lots
-- `ORDER BY acquired_at, id`, where `id` is the lot row's RANDOM uuid. Two buys at
-- the identical timestamp were therefore consumed in an arbitrary order that could
-- change between recomputes, and with different unit costs that changes which cost
-- a sale consumes — so realized gains for same-instant buys were not reproducible.
-- The walk orders by (acquired_at, leg_id), which is stable across runs. Same-
-- instant buys at the same price are unaffected either way; at different prices the
-- new answer is deterministic where the old one was not.
--
-- Lots are built from the buy events rather than read from `lots`, which is exact:
-- the reset step in mig 148 set each lot's quantity and unit_cost FROM its own
-- txn_leg, so the table was already a projection of those legs. Rows for legs on
-- hidden headers are excluded from the walk, and the persist step leaves them
-- untouched — matching mig 148, whose reset UPDATE also skipped them.

-- ---------------------------------------------------------------------------
-- Composite types the walk returns.
-- ---------------------------------------------------------------------------
CREATE TYPE holdings_fifo_lot AS (
    leg_id      UUID,
    quantity    NUMERIC,
    unit_cost   NUMERIC,
    acquired_at TIMESTAMPTZ,
    is_closed   BOOLEAN
);

CREATE TYPE holdings_fifo_gain AS (
    sell_leg_id        UUID,
    sold_at            TIMESTAMPTZ,
    quantity           NUMERIC,
    proceeds           NUMERIC,
    cost_basis_sold    NUMERIC,
    realized_gain      NUMERIC,
    proceeds_lt        NUMERIC,
    cost_basis_sold_lt NUMERIC,
    realized_gain_lt   NUMERIC
);

COMMENT ON TYPE holdings_fifo_lot IS
    'One open FIFO lot as derived by holdings_fifo_walk (mig 202). Keyed by leg_id, '
    'because a lot IS a buy leg; the lots table row is a projection of it.';

-- ---------------------------------------------------------------------------
-- The walk. Pure: reads legs/splits/accounts, writes nothing.
-- ---------------------------------------------------------------------------
CREATE FUNCTION holdings_fifo_walk(
    p_account_id  UUID,
    p_security_id UUID,
    p_as_of       TIMESTAMPTZ DEFAULT NULL,
    OUT o_quantity   NUMERIC,
    OUT o_cost_basis NUMERIC,
    OUT o_lots       holdings_fifo_lot[],
    OUT o_gains      holdings_fifo_gain[]
)
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
AS $$
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
$$;

COMMENT ON FUNCTION holdings_fifo_walk(UUID, UUID, TIMESTAMPTZ) IS
    'ADR-0064 FIFO walk, pure (mig 202). Returns quantity, cost basis, open lots and '
    'realized gains for one (holdings-account, security) as of an instant (NULL = all '
    'history). recompute_holdings_cost_basis persists what it returns; '
    'holdings_cost_basis_as_of reads it. One algorithm, two callers — the walk used to '
    'keep its state in the lots table, which no read could borrow.';

GRANT EXECUTE ON FUNCTION holdings_fifo_walk(UUID, UUID, TIMESTAMPTZ) TO coffer_app;

-- ---------------------------------------------------------------------------
-- The writer, now a thin persist over the walk.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(
    p_ledger_id   UUID DEFAULT NULL,
    p_account_id  UUID DEFAULT NULL,
    p_security_id UUID DEFAULT NULL
)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_walk    RECORD;
    v_updated INTEGER := 0;
    v_resolved_ledger_id UUID;
BEGIN
    -- Auto-create the holding row when the caller pinned a specific
    -- (account, security) but no row exists yet.
    IF p_account_id IS NOT NULL AND p_security_id IS NOT NULL THEN
        SELECT ledger_id INTO v_resolved_ledger_id FROM accounts WHERE id = p_account_id;
        IF v_resolved_ledger_id IS NOT NULL
           AND (p_ledger_id IS NULL OR p_ledger_id = v_resolved_ledger_id)
           AND NOT EXISTS (SELECT 1 FROM holdings
                           WHERE account_id = p_account_id AND security_id = p_security_id)
        THEN
            INSERT INTO holdings (id, account_id, security_id, ledger_id, quantity, cost_basis, as_of)
            VALUES (gen_random_uuid(), p_account_id, p_security_id, v_resolved_ledger_id, 0, 0, NOW());
        END IF;
    END IF;

    FOR v_holding IN
        SELECT id, account_id, security_id, ledger_id
        FROM holdings
        WHERE (p_ledger_id   IS NULL OR ledger_id   = p_ledger_id)
          AND (p_account_id  IS NULL OR account_id  = p_account_id)
          AND (p_security_id IS NULL OR security_id = p_security_id)
    LOOP
        SELECT * INTO v_walk
        FROM holdings_fifo_walk(v_holding.account_id, v_holding.security_id, NULL);

        -- Realized gains are rebuilt from scratch for this (account, security).
        DELETE FROM realized_gains
        WHERE account_id = v_holding.account_id AND security_id = v_holding.security_id;

        IF COALESCE(array_length(v_walk.o_gains, 1), 0) > 0 THEN
            INSERT INTO realized_gains (
                ledger_id, account_id, security_id, sell_leg_id,
                sold_at, quantity, proceeds, cost_basis_sold, realized_gain,
                proceeds_lt, cost_basis_sold_lt, realized_gain_lt)
            SELECT v_holding.ledger_id, v_holding.account_id, v_holding.security_id,
                   g.sell_leg_id, g.sold_at, g.quantity, g.proceeds, g.cost_basis_sold,
                   g.realized_gain, g.proceeds_lt, g.cost_basis_sold_lt, g.realized_gain_lt
            FROM unnest(v_walk.o_gains) AS g;
        END IF;

        -- Lot rows are created by the write path; here they are brought to the state
        -- the walk derived. Lots whose leg is on a hidden header are absent from the
        -- walk and left untouched, as mig 148's reset UPDATE also left them.
        IF COALESCE(array_length(v_walk.o_lots, 1), 0) > 0 THEN
            UPDATE lots l
            SET quantity  = w.quantity,
                unit_cost = w.unit_cost,
                is_closed = w.is_closed
            FROM unnest(v_walk.o_lots) AS w
            WHERE l.holding_id = v_holding.id
              AND l.leg_id     = w.leg_id;
        END IF;

        UPDATE holdings
        SET cost_basis = v_walk.o_cost_basis,
            quantity   = v_walk.o_quantity
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$$;

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID, UUID, UUID) IS
    'ADR-0064 FIFO basis writer. Since mig 202 it is a thin persist over '
    'holdings_fifo_walk: the algorithm lives there and is shared with the as-of read '
    'path, so the two cannot drift.';

-- ---------------------------------------------------------------------------
-- The read path: FIFO basis as of an instant.
-- ---------------------------------------------------------------------------
CREATE FUNCTION holdings_cost_basis_as_of(
    p_ledger_id   UUID,
    p_as_of       TIMESTAMPTZ,
    p_account_ids UUID[] DEFAULT NULL
)
RETURNS TABLE(account_id UUID, security_id UUID, quantity NUMERIC, cost_basis NUMERIC)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    -- Positions are discovered from the LEGS, not from the holdings projection: a
    -- position closed since p_as_of has no projection row but was held then. The
    -- qty <> 0 filter drops anything closed BY p_as_of, matching the valuation feeder.
    SELECT p.account_id, p.security_id, w.o_quantity, w.o_cost_basis
    FROM (
        SELECT DISTINCT l.account_id, l.security_id
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id     = p_ledger_id
          AND l.security_id  IS NOT NULL
          AND l.quantity     IS NOT NULL
          AND h.is_hidden     = FALSE
          AND h.posted_at    <= p_as_of
          AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
    ) p
    CROSS JOIN LATERAL holdings_fifo_walk(p.account_id, p.security_id, p_as_of) w
    WHERE w.o_quantity <> 0;
$$;

COMMENT ON FUNCTION holdings_cost_basis_as_of(UUID, TIMESTAMPTZ, UUID[]) IS
    'FIFO cost basis + quantity per (holdings-account, security) as of an instant '
    '(mig 202), via the same holdings_fifo_walk the writer persists. Positions are '
    'discovered from legs so one closed since the instant is still valued.';

GRANT EXECUTE ON FUNCTION holdings_cost_basis_as_of(UUID, TIMESTAMPTZ, UUID[]) TO coffer_app;

-- One-shot recompute, as migration 148 did when it introduced FIFO. The walk is
-- behaviour-identical to mig 169's except for the lot tie-break on same-instant
-- buys (random lot uuid -> leg_id), so most ledgers will not move a cent. It runs
-- anyway: without it, stored cost_basis and realized_gains keep whatever the old
-- ordering produced while any LATER partial recompute uses the new one, leaving two
-- vintages in the same tax-relevant table with nothing to distinguish them.
SELECT recompute_holdings_cost_basis();
