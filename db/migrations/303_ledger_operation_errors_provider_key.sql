-- =============================================================================
-- 303 — each run error names the source that raised it
-- =============================================================================
--
-- WHY
-- ---
-- A quote refresh fans out to several price sources (the SimpleFIN holdings read,
-- Yahoo Finance, the benchmark and exchange-rate fetch) and records one
-- ledger_operation_errors row per failure. The rows did not say which source
-- raised them, so the Activity log listed every source's errors as one list, and
-- an error from the SimpleFIN holdings read looked like one from Yahoo.
--
-- WHAT CHANGES
-- ------------
--   * ledger_operation_errors.provider_key (TEXT, nullable): the key of the source
--     that raised the error (simplefin-holdings, yahoo, market-series). A quote
--     run's errors carry it; a sync or file import's do not, since the run itself
--     names its one source. NULL on every existing row: the source of an old
--     error is not recorded anywhere to derive it from, and the Activity log lists
--     an error with no source exactly as it did before.
--
-- No snapshot change: ledger_operation_errors is outside the snapshot (ADR-0037).
-- No RLS change: the policies gate on ledger_id, which this does not touch.
-- DDL ONLY. Adding a nullable column with no default rewrites no row.
-- =============================================================================

BEGIN;

ALTER TABLE ledger_operation_errors
    ADD COLUMN provider_key TEXT;

COMMENT ON COLUMN ledger_operation_errors.provider_key IS
    'The source that raised the error (simplefin-holdings, yahoo, market-series); '
    'NULL for sync and import errors and for rows written before migration 303.';

COMMIT;
