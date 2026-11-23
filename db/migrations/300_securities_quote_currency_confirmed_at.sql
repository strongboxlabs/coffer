-- =============================================================================
-- 300 — a security records when Yahoo last named its currency (ADR-0114 D5)
-- =============================================================================
--
-- WHY
-- ---
-- Until the quote-currency fix, every price the market-data fetch stored was stamped
-- USD, whatever the symbol traded in. The fix stamps each new fetch with the currency
-- Yahoo names, but the rows fetched before it keep the USD stamp. The repair (ADR-0114
-- D5, amendment) re-stamps those rows from the currency Yahoo names, and this column is
-- how it knows which securities it has done: a security whose currency a Yahoo read has
-- named carries the instant of that read.
--
-- WHAT CHANGES
-- ------------
--   * securities.quote_currency_confirmed_at (new, TIMESTAMPTZ, NULL): when a Yahoo read
--     last named this security's currency. The quote refresh sets it on every read that
--     names one, and in the same transaction re-stamps the security's older fetch rows
--     (security_prices.source = 'fetch') that carry another currency. For a symbol quoted
--     in its major unit only the currency is re-stamped. For one quoted in a minor unit
--     (GBp) those rows hold the minor unit's figures, so price, high and low are divided by
--     its factor and rounded to 4dp, as a new fetch is stored; volume is left as it is,
--     and so is a row already in the major unit. NULL means no
--     read has named it yet. The quote refresh asks Yahoo once for the currency of each
--     such security that has fetched prices, held or not, and the consistency check's
--     quote_currency projection reports one until it is set.
--
-- No index: every reader reads one ledger's securities and filters them in memory or
-- by id.
--
-- EXISTING INSTALLS
-- -----------------
-- Every existing row takes NULL, so no row is confirmed until a refresh confirms it,
-- and a fresh install and an upgraded one hold the same values. No row is derived from
-- the clock, so the migration is deterministic. The snapshot functions capture and
-- restore securities by row (to_jsonb and jsonb_populate_recordset over the table's own
-- type), so they carry the column without a change, and a snapshot taken before this
-- migration restores it as NULL: those securities are confirmed again by the next
-- refresh.
-- =============================================================================

BEGIN;

ALTER TABLE securities
    ADD COLUMN quote_currency_confirmed_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN securities.quote_currency_confirmed_at IS
    'When a Yahoo read last named this security''s currency (ADR-0114 D5). Set by the '
    'quote refresh on every read that names one, which in the same transaction re-stamps '
    'the currency of the security''s fetch rows in security_prices that carry another, '
    'converting a minor unit''s figures (GBp) to the major unit as a new fetch is stored. '
    'NULL: no read has named it yet, so its fetch rows may still carry the USD stamp every '
    'fetch had before the fix.';

COMMIT;
