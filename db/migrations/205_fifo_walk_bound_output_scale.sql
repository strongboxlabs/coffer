-- 205 — bound what the FIFO writer STORES, so nothing unbounded reaches a client
--
-- Companion to mig 204, which fixed a live OverflowException by rounding
-- holdings_cost_basis_as_of. This closes the same hazard on the write side.
--
-- WHAT THIS IS NOT. It is not a fix for an observed failure. Four fixtures — large
-- magnitudes, non-terminating unit costs, fractional consumed quantities — all
-- produced clean 2dp values in realized_gains, so the write path is not currently
-- emitting long scale and no report is broken by it.
--
-- WHY DO IT ANYWAY. Every money column in realized_gains is plain NUMERIC —
-- quantity, proceeds, cost_basis_sold, realized_gain and the three _lt columns from
-- mig 169 — so the schema bounds nothing. The values are products of a division:
-- consumed cost is take x unit_cost, unit_cost = (amount + fee) / quantity. That
-- shape reaches 30+ digits easily:
--
--   (100.00 / 7) * 3.333333333333     = 47.6190476190428570952380952381    (30 digits)
--   0.333333333333 * (100000000.00/3) = 11111111.111099999999888888888889  (32 digits)
--
-- System.Decimal holds 28-29 and Npgsql throws rather than truncate — exactly how
-- holdings_snapshot broke in 0.63.0. That the same shape stays short here today is a
-- property of the current data, not of the code.
--
-- Before mig 202 the walk read unit_cost from lots.unit_cost, a NUMERIC(25,12)
-- COLUMN, so the schema bounded it on the way in. Moving the walk in-memory removed
-- that bound without replacing it. Rounding here restores it at the boundary where
-- values are persisted, while intermediate arithmetic stays at full precision, so
-- FIFO consumption itself is unchanged.
--
-- Rounded HERE rather than inside holdings_fifo_walk deliberately: redefining that
-- 200-line body in a second migration would mean the current definition could only
-- be reconstructed by diffing two files, which is how a port earlier in this series
-- silently dropped three behaviours by starting from a superseded copy.
--
-- Money 4dp, shares 12dp — the convention of holdings.cost_basis NUMERIC(19,4),
-- lots.unit_cost NUMERIC(25,12), and migrations 172 / 200 / 204.

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

        DELETE FROM realized_gains
        WHERE account_id = v_holding.account_id AND security_id = v_holding.security_id;

        IF COALESCE(array_length(v_walk.o_gains, 1), 0) > 0 THEN
            INSERT INTO realized_gains (
                ledger_id, account_id, security_id, sell_leg_id,
                sold_at, quantity, proceeds, cost_basis_sold, realized_gain,
                proceeds_lt, cost_basis_sold_lt, realized_gain_lt)
            SELECT v_holding.ledger_id, v_holding.account_id, v_holding.security_id,
                   g.sell_leg_id, g.sold_at,
                   ROUND(g.quantity, 12),
                   ROUND(g.proceeds, 4),
                   ROUND(g.cost_basis_sold, 4),
                   ROUND(g.realized_gain, 4),
                   ROUND(g.proceeds_lt, 4),
                   ROUND(g.cost_basis_sold_lt, 4),
                   ROUND(g.realized_gain_lt, 4)
            FROM unnest(v_walk.o_gains) AS g;
        END IF;

        -- Lot rows are created by the write path; here they are brought to the state
        -- the walk derived. Lots whose leg is on a hidden or merged header are absent
        -- from the walk and left untouched. The lots columns carry their own
        -- precision, so the assignment bounds these.
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
        SET cost_basis = ROUND(v_walk.o_cost_basis, 4),
            quantity   = ROUND(v_walk.o_quantity, 12)
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$$;

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID, UUID, UUID) IS
    'ADR-0064 FIFO basis writer. Since mig 202 a thin persist over holdings_fifo_walk, '
    'so the algorithm is shared with the as-of read path and the two cannot drift. '
    'Since mig 205 it bounds what it stores (money 4dp, shares 12dp) because the '
    'realized_gains money columns are unbounded NUMERIC and an unbounded value there '
    'would overflow System.Decimal on read.';
