-- =============================================================================
-- 185 — provider_runs → ledger_operations (ADR-0055 generalization)
-- =============================================================================
--
-- ADR-0055 named the run-audit `provider_runs` because its only writers were
-- external data/quote PROVIDERS (SimpleFIN, OFX/QIF file import, Yahoo quotes).
-- The observability sweep (post ADR-0086) adds two more ledger operations that
-- are NOT provider runs but belong in the same per-ledger activity timeline:
--
--   * Moneydance bootstrap import (ADR-0071) — a one-shot ledger-creating import.
--     Fits the existing model as family `ingest`, provider_key `moneydance`,
--     triggered_via `file-upload` — a sibling to the OFX/QIF file imports already
--     recorded here. No CHECK change needed for it.
--   * Snapshot restore (ADR-0037) — replaces a ledger's data in place. Not an
--     ingest and not a quote, so it needs a new `snapshot` family.
--
-- Rather than a parallel table, the audit surface is generalized: the table (and
-- its two child tables) is renamed to `ledger_operations`, the honest name for
-- "one recorded operation on a ledger" (sync | quote refresh | import | restore).
-- Pure rename + one widened family CHECK — every existing row is preserved and
-- the Settings→Activity panel keeps working against the renamed table.
--
-- Modeled on migration 132 (sync_runs → provider_runs), which renamed the same
-- three tables the same way. All index / constraint / policy names move in
-- lockstep so the schema reads cleanly. fn_ledger_delete (mig 141) references
-- the table by name in its body and is recreated below.
-- =============================================================================

BEGIN;

-- ----- 1. Tables ------------------------------------------------------------
ALTER TABLE provider_runs            RENAME TO ledger_operations;
ALTER TABLE provider_run_errors      RENAME TO ledger_operation_errors;
ALTER TABLE provider_run_promotions  RENAME TO ledger_operation_promotions;

-- ----- 2. Child FK columns --------------------------------------------------
ALTER TABLE ledger_operation_errors      RENAME COLUMN provider_run_id TO ledger_operation_id;
ALTER TABLE ledger_operation_promotions  RENAME COLUMN provider_run_id TO ledger_operation_id;

-- ----- 3. Primary keys ------------------------------------------------------
ALTER TABLE ledger_operations            RENAME CONSTRAINT provider_runs_pkey            TO ledger_operations_pkey;
ALTER TABLE ledger_operation_errors      RENAME CONSTRAINT provider_run_errors_pkey      TO ledger_operation_errors_pkey;
ALTER TABLE ledger_operation_promotions  RENAME CONSTRAINT provider_run_promotions_pkey  TO ledger_operation_promotions_pkey;

-- ----- 4. Check + unique constraints ---------------------------------------
ALTER TABLE ledger_operations RENAME CONSTRAINT provider_runs_status_check TO ledger_operations_status_check;
ALTER TABLE ledger_operations RENAME CONSTRAINT uq_provider_runs_id_ledger TO uq_ledger_operations_id_ledger;
ALTER TABLE ledger_operations RENAME CONSTRAINT ck_provider_runs_triggered_via TO ck_ledger_operations_triggered_via;

-- The family CHECK gains the `snapshot` family (a CHECK cannot be widened in
-- place, so drop + re-add under the new name).
ALTER TABLE ledger_operations DROP CONSTRAINT ck_provider_runs_family;
ALTER TABLE ledger_operations
    ADD CONSTRAINT ck_ledger_operations_family
        CHECK (family IN ('ingest', 'quote', 'snapshot'));

-- ----- 5. Foreign keys ------------------------------------------------------
ALTER TABLE ledger_operations RENAME CONSTRAINT fk_provider_runs_ledger
    TO fk_ledger_operations_ledger;
ALTER TABLE ledger_operations RENAME CONSTRAINT provider_runs_feed_connection_id_ledger_fkey
    TO ledger_operations_feed_connection_id_ledger_fkey;
ALTER TABLE ledger_operations RENAME CONSTRAINT provider_runs_triggered_by_user_id_fkey
    TO ledger_operations_triggered_by_user_id_fkey;

ALTER TABLE ledger_operation_errors RENAME CONSTRAINT provider_run_errors_run_ledger_fkey
    TO ledger_operation_errors_run_ledger_fkey;
ALTER TABLE ledger_operation_errors RENAME CONSTRAINT provider_run_errors_provider_run_id_fkey
    TO ledger_operation_errors_ledger_operation_id_fkey;

ALTER TABLE ledger_operation_promotions RENAME CONSTRAINT provider_run_promotions_run_ledger_fkey
    TO ledger_operation_promotions_run_ledger_fkey;
ALTER TABLE ledger_operation_promotions RENAME CONSTRAINT provider_run_promotions_provider_run_id_fkey
    TO ledger_operation_promotions_ledger_operation_id_fkey;
ALTER TABLE ledger_operation_promotions RENAME CONSTRAINT provider_run_promotions_header_id_ledger_fkey
    TO ledger_operation_promotions_header_id_ledger_fkey;

-- ----- 6. Indexes -----------------------------------------------------------
ALTER INDEX idx_provider_runs_started                   RENAME TO idx_ledger_operations_started;
ALTER INDEX idx_provider_runs_feed_connection_started   RENAME TO idx_ledger_operations_feed_connection_started;
ALTER INDEX uq_provider_runs_one_running_per_connection RENAME TO uq_ledger_operations_one_running_per_connection;
ALTER INDEX idx_provider_run_errors_run                 RENAME TO idx_ledger_operation_errors_run;
ALTER INDEX idx_provider_run_promotions_run             RENAME TO idx_ledger_operation_promotions_run;
ALTER INDEX idx_provider_run_promotions_header          RENAME TO idx_ledger_operation_promotions_header;

-- ----- 7. RLS policies ------------------------------------------------------
ALTER POLICY provider_runs_per_user            ON ledger_operations            RENAME TO ledger_operations_per_user;
ALTER POLICY provider_run_errors_per_user      ON ledger_operation_errors      RENAME TO ledger_operation_errors_per_user;
ALTER POLICY provider_run_promotions_per_user  ON ledger_operation_promotions  RENAME TO ledger_operation_promotions_per_user;

-- ----- 8. Recreate fn_ledger_delete (references the table by name) ----------
-- Only the operational-audit DELETE target changes (provider_runs →
-- ledger_operations); the financial block is unchanged and stays in lockstep
-- with fn_ledger_snapshot_restore (mig 127…).
CREATE OR REPLACE FUNCTION fn_ledger_delete(p_ledger_id uuid)
    RETURNS void
    LANGUAGE plpgsql
AS $$
BEGIN
    -- Operational/audit (RESTRICT → ledgers). ledger_operations first so its
    -- CASCADE children (errors + promotions) — whose promotions.header_id
    -- references txn_headers — are gone before the financial block.
    DELETE FROM ledger_operations            WHERE ledger_id = p_ledger_id;

    -- Financial footprint — same child→parent order as the snapshot restore
    -- (db/migrations/127…), kept in lockstep.
    DELETE FROM loan_terms                   WHERE ledger_id = p_ledger_id;
    DELETE FROM recurring_occurrence_exceptions WHERE ledger_id = p_ledger_id;
    DELETE FROM recurring_transactions       WHERE ledger_id = p_ledger_id;
    DELETE FROM security_splits              WHERE ledger_id = p_ledger_id;
    DELETE FROM lots                         WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_overrides            WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_overrides         WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_legs                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                  WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_group_members   WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups          WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                     WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices              WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids         WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings   WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                         WHERE ledger_id = p_ledger_id;
    DELETE FROM securities                   WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_account_balances  WHERE ledger_id = p_ledger_id;

    -- feed_connections (RESTRICT → ledgers; cascades feed_connection_accounts).
    DELETE FROM feed_connections             WHERE ledger_id = p_ledger_id;

    -- The ledger row. CASCADEs user_ledger_grants, ledger_snapshots,
    -- scheduled_jobs, user_preferences.
    DELETE FROM ledgers                      WHERE id = p_ledger_id;
END;
$$;

-- ----- 9. Comments ----------------------------------------------------------
COMMENT ON TABLE ledger_operations IS
    'ADR-0055/0086: one recorded operation on a ledger — sync | quote refresh | '
    'file/Moneydance import | snapshot restore. Renamed from provider_runs (mig 185).';
COMMENT ON COLUMN ledger_operations.family IS
    'Operation family: ingest | quote | snapshot (ADR-0055/0086).';
COMMENT ON COLUMN ledger_operations.provider_key IS
    'Concrete operation: simplefin | ofx | qif | moneydance | yahoo | snapshot-restore | …';

COMMIT;
