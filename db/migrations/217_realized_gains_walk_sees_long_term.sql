-- =============================================================================
-- 217 — the realized-gains walk returns the long-term columns it always omitted.
-- =============================================================================
--
-- DDL ONLY. This migration redefines one pure function. It writes no rows, touches
-- no money, and is a no-op on data — stated up front because a migration number in
-- the realized-gains area invites the opposite assumption. There is deliberately no
-- backfill; see the note at the end.
--
-- WHY: realized_gains has NINE money-bearing columns. realized_gains_walk (mig 206)
-- returns SIX. The three it omits — proceeds_lt, cost_basis_sold_lt,
-- realized_gain_lt (mig 169) — are the long-term half of every disposal's tax
-- split.
--
-- That omission is not cosmetic, because this function is the ORACLE. The ledger
-- consistency check compares stored realized gains against it, so any drift in the
-- long-term columns is invisible to the check by construction: it cannot compare a
-- column its reference implementation does not produce. A ledger whose entire
-- short/long split had been zeroed would report healthy.
--
-- The hazard is live and is the shape mig 202's header describes: those three columns are NOT NULL DEFAULT 0, so any INSERT that omits them
-- silently writes zeros rather than failing. A writer built on this function — the
-- obvious way to write a repair or a backfill — would destroy the tax split on
-- every row it touched, and the check would agree that everything was fine.
--
-- ROUNDING matches the writer at 2dp (mig 209), which is the whole point of this
-- function existing: the check must compare like for like. The previous comment
-- said it rounded "exactly as mig 205 rounds them on write"; 205 rounded to 4dp and
-- this to 2dp. 209 moved the writer to 2dp, so the comment now names 209.
--
-- DROP + CREATE, not CREATE OR REPLACE: Postgres will not let a RETURNS TABLE shape
-- change under REPLACE. The (UUID, UUID) signature is unchanged, so the EF
-- HasDbFunction binding keeps working and RealizedGainWalkRow gains three
-- properties additively.
--
-- WHAT IS NOT HERE, and why. No startup backfill corrects the cent-level
-- double-rounding mig 209 left in existing rows. The damage is one cent on each
-- affected row, while an unattended money rewrite at boot fails by crash-looping
-- the container and has no bound on its duration. The correction already exists —
-- the repair action produces correct values since 209 fixed the writer — so the
-- scheduled check is made to SEE the problem and hand the user that fix, rather
-- than rewriting money nobody asked it to touch.
-- =============================================================================

BEGIN;

DROP FUNCTION IF EXISTS realized_gains_walk(UUID, UUID);

CREATE FUNCTION realized_gains_walk(
    p_account_id  UUID,
    p_security_id UUID
) RETURNS TABLE (
    sell_leg_id        UUID,
    sold_at            TIMESTAMPTZ,
    quantity           NUMERIC,
    proceeds           NUMERIC,
    cost_basis_sold    NUMERIC,
    realized_gain      NUMERIC,
    proceeds_lt        NUMERIC,
    cost_basis_sold_lt NUMERIC,
    realized_gain_lt   NUMERIC
)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    SELECT (g).sell_leg_id,
           (g).sold_at,
           ROUND((g).quantity, 12),
           ROUND((g).proceeds, 2),
           ROUND((g).cost_basis_sold, 2),
           ROUND((g).realized_gain, 2),
           ROUND((g).proceeds_lt, 2),
           ROUND((g).cost_basis_sold_lt, 2),
           ROUND((g).realized_gain_lt, 2)
      FROM holdings_fifo_walk(p_account_id, p_security_id, NULL) w,
           unnest(w.o_gains) AS g;
$$;

COMMENT ON FUNCTION realized_gains_walk(UUID, UUID) IS
    'Pure per-disposal realized gains (mig 206, widened to the long-term columns by '
    '217), rounded exactly as mig 209 rounds them on write so a consistency check '
    'compares like for like. All nine money-bearing columns: a reference '
    'implementation that omits one cannot detect drift in it.';

COMMIT;
