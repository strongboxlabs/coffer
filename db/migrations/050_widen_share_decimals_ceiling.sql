-- =============================================================================
-- 050 — widen securities.share_decimals CHECK to match the quantity scale
-- =============================================================================
--
-- Migration 012 set `share_decimals INTEGER NOT NULL DEFAULT 4
-- CHECK (share_decimals BETWEEN 0 AND 6)`.
-- Migration 043 bumped `holdings.quantity` / `txn_legs.quantity` /
-- `lots.quantity` / `txn_legs.unit_price` / `security_prices.price`
-- from NUMERIC(19,6) to NUMERIC(25,12) so MD's 11-decimal share
-- displays survive the round trip. But the share_decimals CHECK
-- stayed at 6.
--
-- Real-world MD exports carry `dec=9` for some mutual funds (certain
-- mutual-fund share classes, as found on a real ledger — and almost
-- certainly others elsewhere). The
-- importer's SecurityMapper.ClampShareDecimals silently rewrites
-- out-of-range values to 4, which then mis-scales every transaction
-- on those securities by 10^(real_dec - 4) — a factor of 10^5 on dec=9
-- shapes. Symptom: txn_legs.unit_price stored that factor too small
-- and quantity that factor too large.
--
-- A one-off data scrub (2026-05-19) repaired the rows already
-- imported at the wrong scale. This migration is the
-- structural fix so a future re-import preserves correct values.
-- The importer's clamp is widened in lockstep.
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
    'the older ceiling silently misclamped real-world dec=9 mutual '
    'funds back to 4 and produced 100,000x-scaled qty + price on '
    'every leg.';
