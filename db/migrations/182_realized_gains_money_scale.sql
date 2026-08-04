-- =============================================================================
-- 182 — constrain realized_gains money columns to 2dp (fix decimal overflow)
-- =============================================================================
--
-- realized_gains money columns were unconstrained `numeric` (no scale). The FIFO
-- recompute (ADR-0064) computes cost_basis_sold as SUM(consumed_qty * unit_cost).
-- Migration 180 widened lots.unit_cost to NUMERIC(25,12); a fractional-share lot
-- (quantity is also (25,12)) times a 12dp unit_cost yields up to 24 decimal
-- places. On a 6-figure position that is ~30 significant digits -- past the .NET
-- `decimal` ceiling (~28-29 sig digits) -- so Npgsql threw System.OverflowException
-- while MATERIALISING the realized_gains read (InvestmentReportingRepository
-- .RealizedGainsAsync), i.e. the `realized_gains` MCP tool + report failed
-- outright. Pre-180 (unit_cost 4dp) the product was ~16dp and stayed under the
-- ceiling, so it never surfaced; small synthetic test amounts never reached it.
--
-- These are MONEY (ADR-0073): 2 decimals, authoritative. Constrain them so the
-- DB rounds on store (producer can't persist sub-cent noise) and reads can never
-- overflow decimal again. The ALTER rounds existing rows in place -- the 24dp
-- tail is discarded and the correct 2dp money value (the first two decimals) is
-- kept -- so applying this migration also scrubs the bad rows. quantity is SHARES,
-- not money: pin it to the (25,12) family (migration 043) instead.
--
-- No view/matview depends on realized_gains, so the ALTERs are safe.
-- =============================================================================

ALTER TABLE realized_gains
    ALTER COLUMN proceeds           TYPE numeric(19, 2),
    ALTER COLUMN proceeds_lt        TYPE numeric(19, 2),
    ALTER COLUMN cost_basis_sold    TYPE numeric(19, 2),
    ALTER COLUMN cost_basis_sold_lt TYPE numeric(19, 2),
    ALTER COLUMN realized_gain      TYPE numeric(19, 2),
    ALTER COLUMN realized_gain_lt   TYPE numeric(19, 2),
    ALTER COLUMN quantity           TYPE numeric(25, 12);
