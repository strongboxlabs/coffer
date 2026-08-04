-- =============================================================================
-- 048 — securities: per-ledger uniqueness for cusip + ticker
-- =============================================================================
--
-- Slice A3 audit surfaced two latent multi-tenant gaps on `securities`:
--
--   1. `uq_securities_cusip` (migration 002) was GLOBALLY unique, even
--      though every other identifier became per-ledger in migration 014's
--      Phase A multi-ledger pass. Two different ledgers should be able to
--      hold the same CUSIP (e.g. both users own AAPL = 037833100); the
--      global index would have rejected the second insert.
--
--   2. No ticker uniqueness existed at all — two rows in the same ledger
--      could both claim "IDXA", silently breaking the security picker
--      A4's editor will reach for. Add the per-ledger constraint now so
--      DB owns correctness alongside the API-layer check.
--
-- Both indexes are partial (WHERE … IS NOT NULL) — tickerless/cusipless
-- securities (private equity, manual entries) coexist freely.
--
-- Ticker matching is case-insensitive via LOWER() — "idxa" and "IDXA"
-- refer to the same security; surface mismatches as conflicts at insert
-- time rather than leaving a hidden duplicate the user can't see.
-- =============================================================================

DROP INDEX IF EXISTS uq_securities_cusip;

CREATE UNIQUE INDEX uq_securities_cusip_per_ledger
    ON securities(ledger_id, cusip)
    WHERE cusip IS NOT NULL;

CREATE UNIQUE INDEX uq_securities_ticker_per_ledger
    ON securities(ledger_id, LOWER(ticker))
    WHERE ticker IS NOT NULL;

COMMENT ON INDEX uq_securities_cusip_per_ledger IS
    'Migration 048: replaces the global uq_securities_cusip from 002. '
    'Two ledgers in the same Coffer deployment can each hold the same '
    'CUSIP (a single security in the real world, two separate user-scoped '
    'rows in our store).';

COMMENT ON INDEX uq_securities_ticker_per_ledger IS
    'Migration 048: per-ledger ticker uniqueness for the Securities catalog '
    '(slice A3) + future security-picker in the investment editor (A4). '
    'Case-insensitive (LOWER) so "idxa" and "IDXA" collide on insert.';
