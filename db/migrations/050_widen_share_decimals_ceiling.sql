-- =============================================================================
-- 050 — widen securities.share_decimals CHECK to match the quantity scale
-- =============================================================================
--
-- Migration 012 set `share_decimals INTEGER NOT NULL DEFAULT 4
-- CHECK (share_decimals BETWEEN 0 AND 6)`.
-- Migration 043 bumped `holdings.quantity` / `txn_legs.quantity` /
-- `lots.quantity` / `txn_legs.unit_price` / `security_prices.price`
-- from NUMERIC(19,6) to NUMERIC(25,12) so share counts and prices
-- keep twelve decimal places. But the share_decimals CHECK
-- stayed at 6.
--
-- MD records a security's share precision in `dec`, and nothing bounds
-- it at 6: a security quoted to more decimals exports a larger `dec`.
-- The importer's SecurityMapper.ClampShareDecimals silently rewrites
-- out-of-range values to 4, which then mis-scales every transaction
-- on those securities by 10^(real_dec - 4): txn_legs.unit_price is
-- stored that factor too small and quantity that factor too large.
--
-- This migration is the structural fix, so an import preserves
-- correct values; it does not rewrite rows already stored at the
-- wrong scale. The importer's clamp is widened in lockstep.
-- =============================================================================

ALTER TABLE securities
    DROP CONSTRAINT IF EXISTS securities_share_decimals_check;

ALTER TABLE securities
    ADD CONSTRAINT securities_share_decimals_check
    CHECK (share_decimals BETWEEN 0 AND 12);

COMMENT ON COLUMN securities.share_decimals IS
    'Per-security share-precision (MD `dec` field). Bound to [0,12] '
    '— matches the NUMERIC(25,12) scale of quantity / unit_price / '
    'price columns post-migration 043. Was [0,6] until migration 050; '
    'the importer clamped any larger dec back to 4, which mis-scaled '
    'qty and price on every leg of that security by 10^(dec - 4).';
