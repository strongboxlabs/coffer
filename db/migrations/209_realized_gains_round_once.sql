-- 209 — round realized_gains money to the scale its columns actually have
--
-- Migration 205 rounds six realized_gains money columns to 4dp. Those columns are
-- NUMERIC(19,2), so Postgres rounds a SECOND time on assignment, and double rounding
-- lands a cent away from the correctly single-rounded value whenever the tail sits in
-- the band where the two orders disagree.
--
-- OBSERVED, not theoretical. On a real 42k-transaction / 586-disposal ledger, three
-- rows (~0.5%) disagreed with a fresh walk:
--
--   stored 0.07  vs 0.06     stored 0.03 vs 0.02     stored -0.32 vs -0.31
--
-- and the consistency report pinned realized_gains permanently unhealthy, because the
-- repair routes back through this same writer and re-stored the same wrong values.
--
-- HOW IT HAPPENED, because the reasoning was careful and still wrong. 205 states the
-- convention as "Money 4dp, shares 12dp — the convention of holdings.cost_basis
-- NUMERIC(19,4), lots.unit_cost NUMERIC(25,12)". That convention is real. It just is
-- not this table's: mig 182 had already narrowed every realized_gains money column to
-- NUMERIC(19,2), three migrations earlier, and its own header says so ("realized_gains
-- money columns were unconstrained `numeric` (no scale)").
--
-- So 205's stated premise — that these columns are "unbounded NUMERIC" and an
-- unbounded value there would overflow System.Decimal on read — was already false when
-- it was written. mig 182 bounds them. mig 202 wrote them UNROUNDED and the column's
-- own scale did the single, correct rounding. 205 added a redundant round, at a finer
-- scale than the destination, and that redundancy is the entire bug. Rounding to a
-- COARSER scale than a column is harmless; to a FINER scale it is a second rounding.
--
-- 205's fixtures could not have caught it: its header notes they "all produced clean
-- 2dp values", so none of them had a tail in the disagreement band. Real data supplies
-- one about every two hundred disposals.
--
-- WHAT CHANGED HERE. Six scale digits, 4 -> 2, in the INSERT. Nothing else. 205 warned
-- that redefining this body in a later migration risks "starting from a superseded
-- copy" — so this definition was extracted verbatim from 205 and patched
-- mechanically, and a reviewer can confirm the claim the same way:
--
--   diff <(sed -n '38,118p' db/migrations/205_fifo_walk_bound_output_scale.sql) \
--        <(sed -n '/^CREATE OR REPLACE FUNCTION recompute/,/^[$][$];/p' \
--                db/migrations/209_realized_gains_round_once.sql)
--
-- quantity stays at 12: NUMERIC(25,12) is its column, so that one always matched.
--
-- NOT BACKFILLED HERE. Existing rows keep their double-rounded values until something
-- recomputes them. With the writer fixed, the "Repair realized gains" action in
-- Settings -> Maintenance now produces values the check agrees with — it routes through
-- this function — so the drift is both visible and fixable on demand. A blanket
-- recompute at migration time would rewrite holdings and lots for every pair as well,
-- which is a much larger blast radius than the cent it corrects.
-- ---------------------------------------------------------------------------

BEGIN;

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
                   ROUND(g.proceeds, 2),
                   ROUND(g.cost_basis_sold, 2),
                   ROUND(g.realized_gain, 2),
                   ROUND(g.proceeds_lt, 2),
                   ROUND(g.cost_basis_sold_lt, 2),
                   ROUND(g.realized_gain_lt, 2)
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
    'Bounds what it stores at each column''s OWN scale (money 2dp per mig 182, shares '
    '12dp) — mig 205 rounded money to 4dp believing these columns were unbounded, but '
    'mig 182 had already narrowed them to NUMERIC(19,2), so that rounded twice and '
    'shifted roughly one disposal in two hundred by a cent (mig 209).';

COMMIT;
