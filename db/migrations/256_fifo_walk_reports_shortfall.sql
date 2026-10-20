-- =============================================================================
-- 256 — the FIFO walk reports what a sale could not match, and can leave a
--       transaction out
-- =============================================================================
--
-- WHY
-- ---
-- A disposal larger than the open lots was silently accepted, and it corrupted
-- two numbers. The walk drains every lot and stops with the excess still in
-- v_remaining, which nothing reads. o_quantity then takes the FULL sale, so the
-- holding goes NEGATIVE — a short position the app does not model — and the gain
-- row records the full quantity and proceeds against only the consumed basis,
-- booking the unmatched shares at zero cost and overstating the realized gain by
-- their entire proceeds. No error anywhere. The API even defines
-- `investment-txn-insufficient-shares`, and nothing has ever returned it.
--
-- The editor's lot preview could not stand in for a guard either: it read the
-- `lots` table, which FIFO drains IN PLACE, so it only ever knew today. Editing a
-- years-old sale on a since-closed account showed "no open lots" and a false
-- "exceeds open position" warning on every historical edit, while a real oversell
-- through the same editor went through unremarked.
--
-- WHAT CHANGES
-- ------------
--   * holdings_fifo_walk gains OUT o_shortfall — the shares its disposals could
--     not match, summed — and p_exclude_header_id, which leaves one header's legs
--     out of the stream. Nothing else about the walk moves: the body is ported
--     MECHANICALLY from its current definition (migration 230) with four
--     asserted edits, because migration 202's first draft ported a superseded
--     definition and silently dropped three behaviours.
--
--   * holdings_fifo_shortfall(account, security) — the writer's question: how
--     short is this position now? The API asks it before and after a save and
--     refuses a save that makes it worse.
--
--   * holdings_fifo_open_lots(account, security, as_of, exclude_header) — the
--     preview's question: what was open at this instant, without the
--     transaction being edited? Pure, so a report-style read takes no locks.
--
--   * holdings_fifo_shortfalls(ledger) — the consistency report's question:
--     which positions are short already? A sale accepted before this migration,
--     or written by an importer, stays short, and it overstates its gain. Decided
--     2026-10-20 that those are REPORTED, not repaired: the purchase they are
--     missing is not in the data, so any repair would invent a cost basis.
--
-- WHY THE WRITER REFUSES "WORSE", NOT "SHORT"
-- -------------------------------------------
-- Imported data can already carry small shortfalls — share residues from the
-- source system. Refusing any save on a short position would lock the user out
-- of exactly the rows they are trying to clean up. A save may leave a shortfall
-- where it was; it may not create one or deepen one.
--
-- DROP + CREATE, NOT CREATE OR REPLACE
-- ------------------------------------
-- Postgres will not change a function's OUT shape in place. The three callers —
-- recompute_holdings_cost_basis (227, plpgsql), holdings_cost_basis_as_of (230)
-- and realized_gains_walk (217), both string-bodied LANGUAGE sql — hold no
-- dependency on it, so the DROP succeeds and each resolves the new function on
-- its next call: a three-argument call fills p_exclude_header_id with its
-- default, and the columns they read are all still there.
--
-- No data changes. Every stored holding, lot and gain is exactly as it was; the
-- shortfall is derived on demand, never persisted.
-- =============================================================================

DROP FUNCTION holdings_fifo_walk(uuid, uuid, timestamp with time zone);

CREATE FUNCTION public.holdings_fifo_walk(p_account_id uuid, p_security_id uuid, p_as_of timestamp with time zone DEFAULT NULL::timestamp with time zone, p_exclude_header_id uuid DEFAULT NULL::uuid, OUT o_quantity numeric, OUT o_cost_basis numeric, OUT o_lots holdings_fifo_lot[], OUT o_gains holdings_fifo_gain[], OUT o_shortfall numeric)
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
    o_shortfall  := 0;

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
    'p_exclude_header_id leaves one header out of the stream.';

-- The writer's question. Unbounded, like the writer: the save is checked against
-- exactly what recompute_holdings_cost_basis will persist. Rounded to the
-- quantity column's scale, so a re-ordering of the same arithmetic cannot read as
-- a change.
CREATE FUNCTION holdings_fifo_shortfall(
    p_account_id  uuid,
    p_security_id uuid
) RETURNS TABLE (shortfall numeric)
LANGUAGE sql
STABLE
AS $$
    SELECT ROUND(w.o_shortfall, 12)
    FROM holdings_fifo_walk(p_account_id, p_security_id, NULL, NULL) w
$$;

GRANT EXECUTE ON FUNCTION holdings_fifo_shortfall(uuid, uuid) TO coffer_app;

COMMENT ON FUNCTION holdings_fifo_shortfall(uuid, uuid) IS
    'Mig 256. Shares the FIFO walk could not match across every disposal of one '
    '(holdings account, security). The investment write paths read it before and '
    'after a save and refuse an increase.';

-- The preview's question. Same scale as the lots table, so a preview built from
-- this agrees with what the writer would store.
CREATE FUNCTION holdings_fifo_open_lots(
    p_account_id        uuid,
    p_security_id       uuid,
    p_as_of             timestamp with time zone,
    p_exclude_header_id uuid
) RETURNS TABLE (
    leg_id      uuid,
    acquired_at timestamp with time zone,
    quantity    numeric,
    unit_cost   numeric
)
LANGUAGE sql
STABLE
AS $$
    SELECT l.leg_id,
           l.acquired_at,
           ROUND(l.quantity, 12),
           ROUND(l.unit_cost, 12)
    FROM holdings_fifo_walk(p_account_id, p_security_id, p_as_of, p_exclude_header_id) w,
         unnest(w.o_lots) AS l
    WHERE NOT l.is_closed
      AND l.quantity > 0
    ORDER BY l.acquired_at, l.leg_id
$$;

GRANT EXECUTE ON FUNCTION holdings_fifo_open_lots(uuid, uuid, timestamp with time zone, uuid)
    TO coffer_app;

-- The consistency report's question. Every (account, security) any leg of the
-- ledger moves shares on, not only those with a holdings row: a position sold
-- short and bought back may net to nothing and still be short in its history.
CREATE FUNCTION holdings_fifo_shortfalls(
    p_ledger_id uuid
) RETURNS TABLE (
    account_id  uuid,
    security_id uuid,
    shortfall   numeric
)
LANGUAGE sql
STABLE
AS $$
    WITH pairs AS (
        SELECT DISTINCT l.account_id, l.security_id
        FROM txn_legs l
        WHERE l.ledger_id = p_ledger_id
          AND l.security_id IS NOT NULL
          AND l.quantity IS NOT NULL
    )
    SELECT p.account_id, p.security_id, ROUND(w.o_shortfall, 12)
    FROM pairs p
    CROSS JOIN LATERAL holdings_fifo_walk(p.account_id, p.security_id, NULL, NULL) w
    WHERE ROUND(w.o_shortfall, 12) > 0
$$;

GRANT EXECUTE ON FUNCTION holdings_fifo_shortfalls(uuid) TO coffer_app;

COMMENT ON FUNCTION holdings_fifo_shortfalls(uuid) IS
    'Mig 256. Every position of one ledger whose disposals took more shares than '
    'were held at the time, and by how much. The consistency report lists them; '
    'nothing repairs them, because the missing purchase is not in the data.';

COMMENT ON FUNCTION holdings_fifo_open_lots(uuid, uuid, timestamp with time zone, uuid) IS
    'Mig 256. Open FIFO lots of one (holdings account, security) as of an instant, '
    'optionally without one header — what was available to a sale being edited. '
    'Drives the editor''s lot preview, which used to read today''s lots table.';
