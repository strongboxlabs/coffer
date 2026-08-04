-- =============================================================================
-- 123 — lots.leg_id ON DELETE CASCADE (align DB FK with the EF model)
-- =============================================================================
--
-- A `lots` row is derived audit data: one row per share-acquiring leg
-- (Buy / BuyXfr / DividendReinvest), keyed to the exact `txn_legs.id`
-- it was computed from (`lots.leg_id`). If that leg is deleted, the lot
-- is orphaned — it has no acquisition event left to describe.
--
-- The leg_id FK was created RESTRICT in migration 025 and carried over
-- as composite-RESTRICT in migration 049, both without a documented
-- rationale (unlike the sibling FKs in 049, which justify their
-- RESTRICT/CASCADE choices inline). The EF model
-- (`AppDbContext` -> `LotRow`) has always declared this relationship as
-- `DeleteBehavior.Cascade`. The DB is the side that diverged.
--
-- The divergence is load-bearing: deleting an investment buy header
-- cascades header -> legs (`txn_legs_header_id_fkey` is CASCADE), but
-- the leg delete then hits this RESTRICT FK from the lot the buy
-- produced, so the whole delete aborts with a 23503 FK violation —
-- a 500 on a legitimate "delete this buy" action. The post-save
-- holdings recompute (HoldingsRecomputeInterceptor) never gets to run
-- because the SaveChanges that would trigger it can't commit.
--
-- CASCADE is correct and safe across every path that touches lots:
--   * Header hard-delete: leg cascade now carries its lot away; the
--     post-save recompute rebuilds holdings/lots from surviving legs.
--   * Re-import / recompute: the recompute function deletes lots
--     directly (`DELETE FROM lots WHERE leg_id = ANY(...)`) and never
--     deletes legs, so its behavior is identical under CASCADE.
--   * No code path relies on RESTRICT to block a leg delete — bank
--     legs have no lots, and the only investment-leg deleters are this
--     hard-delete path and re-import (which clears lots first).
--
-- Recreated as the same composite (leg_id, ledger_id) FK from
-- migration 049 — only the ON DELETE action changes.
-- =============================================================================

ALTER TABLE lots
    DROP CONSTRAINT lots_leg_id_fkey,
    ADD CONSTRAINT lots_leg_id_fkey
        FOREIGN KEY (leg_id, ledger_id) REFERENCES txn_legs(id, ledger_id)
        ON DELETE CASCADE;
