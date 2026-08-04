-- =============================================================================
-- 132 — Generic provider-run audit (ADR-0055): sync_runs → provider_runs
-- Renames the ingest run-audit into a generic provider-run audit that every
-- provider family writes (ingest today; quotes in slice B; future workers).
-- Pure rename + additive columns — existing ingest history is preserved.
--
-- Renamed: the three tables, their child FK columns, and all index /
-- constraint / policy names (kept in lockstep so the schema reads cleanly).
-- Added: family / provider_key / triggered_via (backfilled for existing
-- ingest rows) + nullable quote counters. `triggered_via` (not `trigger` —
-- a reserved word) records how a run started; `triggered_by_user_id`
-- (existing) records WHO — the real user, or the system user for scheduled
-- runs (ADR-0055 D2).
-- =============================================================================

BEGIN;

-- ----- 1. Tables ------------------------------------------------------------
ALTER TABLE sync_runs            RENAME TO provider_runs;
ALTER TABLE sync_run_errors      RENAME TO provider_run_errors;
ALTER TABLE sync_run_promotions  RENAME TO provider_run_promotions;

-- ----- 2. Child FK columns --------------------------------------------------
ALTER TABLE provider_run_errors      RENAME COLUMN sync_run_id TO provider_run_id;
ALTER TABLE provider_run_promotions  RENAME COLUMN sync_run_id TO provider_run_id;

-- ----- 3. Primary keys ------------------------------------------------------
ALTER TABLE provider_runs            RENAME CONSTRAINT sync_runs_pkey            TO provider_runs_pkey;
ALTER TABLE provider_run_errors      RENAME CONSTRAINT sync_run_errors_pkey      TO provider_run_errors_pkey;
ALTER TABLE provider_run_promotions  RENAME CONSTRAINT sync_run_promotions_pkey  TO provider_run_promotions_pkey;

-- ----- 4. Check + unique constraints ---------------------------------------
ALTER TABLE provider_runs RENAME CONSTRAINT sync_runs_status_check  TO provider_runs_status_check;
ALTER TABLE provider_runs RENAME CONSTRAINT uq_sync_runs_id_ledger  TO uq_provider_runs_id_ledger;

-- ----- 5. Foreign keys ------------------------------------------------------
ALTER TABLE provider_runs RENAME CONSTRAINT fk_sync_runs_ledger
    TO fk_provider_runs_ledger;
ALTER TABLE provider_runs RENAME CONSTRAINT sync_runs_feed_connection_id_ledger_fkey
    TO provider_runs_feed_connection_id_ledger_fkey;
ALTER TABLE provider_runs RENAME CONSTRAINT sync_runs_triggered_by_user_id_fkey
    TO provider_runs_triggered_by_user_id_fkey;

ALTER TABLE provider_run_errors RENAME CONSTRAINT sync_run_errors_run_ledger_fkey
    TO provider_run_errors_run_ledger_fkey;
ALTER TABLE provider_run_errors RENAME CONSTRAINT sync_run_errors_sync_run_id_fkey
    TO provider_run_errors_provider_run_id_fkey;

ALTER TABLE provider_run_promotions RENAME CONSTRAINT sync_run_promotions_run_ledger_fkey
    TO provider_run_promotions_run_ledger_fkey;
ALTER TABLE provider_run_promotions RENAME CONSTRAINT sync_run_promotions_sync_run_id_fkey
    TO provider_run_promotions_provider_run_id_fkey;
ALTER TABLE provider_run_promotions RENAME CONSTRAINT sync_run_promotions_header_id_ledger_fkey
    TO provider_run_promotions_header_id_ledger_fkey;

-- ----- 6. Indexes -----------------------------------------------------------
ALTER INDEX idx_sync_runs_started                   RENAME TO idx_provider_runs_started;
ALTER INDEX idx_sync_runs_feed_connection_started   RENAME TO idx_provider_runs_feed_connection_started;
ALTER INDEX uq_sync_runs_one_running_per_connection RENAME TO uq_provider_runs_one_running_per_connection;
ALTER INDEX idx_sync_run_errors_run                 RENAME TO idx_provider_run_errors_run;
ALTER INDEX idx_sync_run_promotions_run             RENAME TO idx_provider_run_promotions_run;
ALTER INDEX idx_sync_run_promotions_header          RENAME TO idx_provider_run_promotions_header;

-- ----- 7. RLS policies ------------------------------------------------------
ALTER POLICY sync_runs_per_user            ON provider_runs            RENAME TO provider_runs_per_user;
ALTER POLICY sync_run_errors_per_user      ON provider_run_errors      RENAME TO provider_run_errors_per_user;
ALTER POLICY sync_run_promotions_per_user  ON provider_run_promotions  RENAME TO provider_run_promotions_per_user;

-- ----- 8. Generic family columns (backfill existing ingest rows) ------------
ALTER TABLE provider_runs ADD COLUMN family        TEXT;
ALTER TABLE provider_runs ADD COLUMN provider_key  TEXT;
ALTER TABLE provider_runs ADD COLUMN triggered_via TEXT;

UPDATE provider_runs SET
    family        = 'ingest',
    provider_key  = CASE WHEN feed_connection_id IS NOT NULL THEN 'simplefin' ELSE 'file' END,
    triggered_via = CASE WHEN feed_connection_id IS NOT NULL THEN 'manual'    ELSE 'file-upload' END;

ALTER TABLE provider_runs
    ALTER COLUMN family        SET NOT NULL,
    ALTER COLUMN provider_key  SET NOT NULL,
    ALTER COLUMN triggered_via SET NOT NULL,
    ADD CONSTRAINT ck_provider_runs_family
        CHECK (family IN ('ingest', 'quote')),
    ADD CONSTRAINT ck_provider_runs_triggered_via
        CHECK (triggered_via IN ('manual', 'file-upload', 'post-sync', 'scheduled'));

-- ----- 9. Provider-specific detail → jsonb; migrate + drop typed counters ---
-- The per-provider breakdown is open-ended (each provider's counters differ
-- and grow), so it lives in one jsonb field rather than nullable typed columns
-- (ADR-0055 D1). Move the existing ingest counters in, then drop them; the
-- vestigial txns_merged / txns_queued are dropped outright.
ALTER TABLE provider_runs ADD COLUMN details JSONB NOT NULL DEFAULT '{}'::jsonb;

UPDATE provider_runs SET details = jsonb_build_object(
    'txns_fetched',       txns_fetched,
    'txns_inserted',      txns_inserted,
    'txns_skipped',       txns_skipped,
    'txns_promoted',      txns_promoted,
    'txns_already_known', txns_already_known,
    'txns_still_pending', txns_still_pending);

ALTER TABLE provider_runs
    DROP COLUMN txns_fetched,
    DROP COLUMN txns_inserted,
    DROP COLUMN txns_merged,
    DROP COLUMN txns_queued,
    DROP COLUMN txns_skipped,
    DROP COLUMN txns_promoted,
    DROP COLUMN txns_already_known,
    DROP COLUMN txns_still_pending;

COMMENT ON COLUMN provider_runs.family IS
    'Provider family: ingest | quote (ADR-0055).';
COMMENT ON COLUMN provider_runs.provider_key IS
    'Concrete provider: simplefin | ofx | qif | yahoo | simplefin-holdings | …';
COMMENT ON COLUMN provider_runs.triggered_via IS
    'How the run started: manual | file-upload | post-sync | scheduled. '
    'WHO is triggered_by_user_id (real user, or the system user for scheduled).';
COMMENT ON COLUMN provider_runs.details IS
    'Provider-specific counts/metadata (jsonb). Ingest: txns_*; quote: '
    'prices_inserted/prices_updated/securities_unresolved.';

COMMIT;
