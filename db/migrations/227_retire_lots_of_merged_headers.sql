-- =============================================================================
-- 227 — a merged or hidden header's LOT stops being a live lot.
-- =============================================================================
--
-- WHAT WAS WRONG. `recompute_holdings_cost_basis` applies the FIFO walk's output
-- to the `lots` table by updating the rows the walk produced. Lots whose leg
-- sits on a hidden or merged header are excluded from the walk (mig 163), and
-- the function said so in a comment — "left untouched" — without noticing that
-- untouched means LEFT AT THE QUANTITY THEY HELD BEFORE.
--
-- Merge a duplicate 50-share buy into a 100-share winner and `holdings.quantity`
-- correctly becomes 100, while the loser's 50-share lot stays open next to the
-- winner's: 150 shares of lots against a 100-share position. Same for hiding a
-- buy. The lots table is DERIVED — the walk reads `txn_legs`, never `lots` — so
-- no later recompute brought it back into agreement; it simply stayed wrong.
--
-- WHY IT MATTERS BEYOND TIDINESS. `lots` is what a reader and a report see for
-- cost basis and holding age. A phantom open lot misstates both, and it is
-- exactly the kind of wrong number that looks plausible: the position total is
-- right, so nothing draws the eye to the lots beneath it.
--
-- THE FIX is the complement of the existing update: after applying the lots the
-- walk produced, retire the open lots on this holding that it did NOT. That is
-- the whole rule — "a lot the walk does not know about is not a live lot" — and
-- it reverses itself correctly if the header is later unhidden or unmerged,
-- because the walk then produces the lot again and the first update restores it.
--
-- NOT the FIFO consumption path. The walk is in-memory over `txn_legs` and never
-- consulted `lots`, so no basis math changes here; what changes is that the
-- stored lots stop contradicting the holding they belong to.
--
-- EXTRACTED FROM THE LIVE CATALOGUE with pg_get_functiondef, not copied from
-- mig 163 or 169 — the body has drifted from both (163's "Lot reset to acquired
-- state" block no longer exists; the FIFO walk moved into `holdings_fifo_walk`).
-- Copying an older migration forward is how mig 205 silently reverted a fix and
-- 209 had to undo it.
--
-- BACKFILL. Existing databases carry whatever phantom lots they already have, so
-- the migration runs the recompute once over every holding to settle them.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION public.recompute_holdings_cost_basis(p_ledger_id uuid DEFAULT NULL::uuid, p_account_id uuid DEFAULT NULL::uuid, p_security_id uuid DEFAULT NULL::uuid)
 RETURNS integer
 LANGUAGE plpgsql
AS $function$
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
        -- the walk derived. The lots columns carry their own precision, so the
        -- assignment bounds these.
        IF COALESCE(array_length(v_walk.o_lots, 1), 0) > 0 THEN
            UPDATE lots l
            SET quantity  = w.quantity,
                unit_cost = w.unit_cost,
                is_closed = w.is_closed
            FROM unnest(v_walk.o_lots) AS w
            WHERE l.holding_id = v_holding.id
              AND l.leg_id     = w.leg_id;
        END IF;

        -- ...and retire the lots the walk did NOT produce. Those belong to legs on
        -- a hidden or merged header: the walk excludes them (mig 163), so until now
        -- they were "left untouched", which meant left at whatever quantity they
        -- held BEFORE the header was hidden or merged.
        --
        -- Holdings were right and the lots were not. Merging a duplicate 50-share
        -- buy into a 100-share winner left holdings.quantity at 100 and left the
        -- loser's 50-share lot open beside the winner's — 150 shares of lots
        -- against a 100-share position. The lots table is derived (the FIFO walk
        -- reads txn_legs, not lots), so nothing recomputed itself back into
        -- agreement, and every reader of lots saw the phantom.
        UPDATE lots l
        SET quantity  = 0,
            is_closed = TRUE
        WHERE l.holding_id = v_holding.id
          AND NOT l.is_closed
          AND NOT EXISTS (
              SELECT 1 FROM unnest(v_walk.o_lots) AS w WHERE w.leg_id = l.leg_id
          );

        UPDATE holdings
        SET cost_basis = ROUND(v_walk.o_cost_basis, 4),
            quantity   = ROUND(v_walk.o_quantity, 12)
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$function$;


-- Settle the phantoms already stored. Idempotent by construction: the function
-- is a recompute, so running it over every holding converges rather than
-- accumulating. NULL for all three scopes means "every holding".
SELECT recompute_holdings_cost_basis(NULL, NULL, NULL);

COMMIT;
