-- =============================================================================
-- 180 — bump lots.unit_cost to NUMERIC(25,12) (finish migration 043)
-- =============================================================================
--
-- Migration 043 ("precision_bump") widened the investment money columns from
-- lossy (19,4)/(19,6) to NUMERIC(25,12) -- txn_legs.quantity, txn_legs.unit_price,
-- holdings.quantity, lots.quantity, security_prices.price -- explicitly because
-- "(19,4) was already lossy." It MISSED lots.unit_cost, which is still (19,4).
--
-- Impact: recompute_holdings_cost_basis (ADR-0064, mig 148) re-derives every
-- lot's unit_cost as leg_amount / quantity and stores it truncated to 4dp; it
-- then costs FIFO sells as consumed_qty * unit_cost. At 4dp the per-lot basis
-- error is up to quantity * 5e-5 -- ~$2 on a 40k-share lot -- which accumulates
-- across a fund's reinvestment lots. It surfaced most visibly on an in-kind
-- transfer: the source side consumes lots at full-precision quantity*unit_cost
-- while the destination carries round(quantity*unit_cost, 2) per lot, so the
-- carried basis drifted ~$4 from the source basis on a ~$570k position.
--
-- Fix: widen the column to (25,12) to match the mig-043 family, then re-derive
-- every lot's unit_cost + rebuild FIFO basis / realized_gains with the same
-- one-shot full recompute mig 148 uses. At 12dp the reconstruction error is
-- quantity * 5e-13 -- penny-perfect for any real position.
-- =============================================================================

ALTER TABLE lots ALTER COLUMN unit_cost TYPE NUMERIC(25, 12);

-- Re-derive unit_cost at the new precision + rebuild FIFO basis + realized_gains
-- for every holding (same backfill call as mig 148 §"One-shot full recompute").
SELECT recompute_holdings_cost_basis();
