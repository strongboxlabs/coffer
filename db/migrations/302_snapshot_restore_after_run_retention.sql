-- =============================================================================
-- 302 — a snapshot restores after audit retention has pruned the runs it names
-- =============================================================================
--
-- WHY
-- ---
-- A snapshot captures txn_headers with their run stamp (ledger_operation_id, mig
-- 221) and ledger_operation_promotions (mig 247), but not the runs themselves:
-- ledger_operations stays outside the snapshot (ADR-0037). AuditRetentionService
-- deletes runs older than Api:AuditRetentionDays. On the live rows each FK applies
-- its own ON DELETE action: a header's stamp is SET NULL, a promotion is deleted
-- (CASCADE). A snapshot taken before the prune still holds the stamp and the
-- promotion, so restoring it re-inserted references to runs that no longer exist,
-- and the restore failed:
--
--   ERROR: insert or update on table "txn_headers" violates foreign key
--          constraint "txn_headers_ledger_operation_id_fkey"
--   ERROR: insert or update on table "ledger_operation_promotions" violates
--          foreign key constraint "ledger_operation_promotions_ledger_operation_id_fkey"
--
-- Every snapshot holding an import, a sync or a promotion older than the retention
-- window was unrestorable from the day the prune ran. Migration 247 recorded these
-- three FKs as references whose parent no app path deletes; the retention prune is
-- one.
--
-- WHAT CHANGES
-- ------------
-- The handling migration 247 gave accounts.feed_connection_id: the restore applies
-- each FK's own ON DELETE action to a restored row whose run has gone.
--
--   * The three FKs become DEFERRABLE INITIALLY DEFERRED, as migration 247 made
--     accounts_feed_connection_id_ledger_fkey:
--       txn_headers_ledger_operation_id_fkey
--       ledger_operation_promotions_ledger_operation_id_fkey
--       ledger_operation_promotions_run_ledger_fkey
--     The restored rows land before the fix-up runs, and the restores' existing
--     SET CONSTRAINTS ALL IMMEDIATE fires the check inside the restore. Any other
--     write is checked at its commit rather than at the statement; the error is the
--     same. ON DELETE actions are never deferred, so the prune's SET NULL and
--     CASCADE are unchanged. SchemaDriftGuardTests fails on a reconciled FK, or an
--     FK the restore's insert order does not satisfy, that is not INITIALLY
--     DEFERRED.
--   * fn_ledger_snapshot_reconcile_external_refs gains two statements:
--       - a restored header whose run is gone gets ledger_operation_id NULL (the
--         FK's ON DELETE SET NULL). The row stays; undo of that import had already
--         expired with the run, as it has on the live row.
--       - a restored promotion whose run is gone is deleted (the FKs' ON DELETE
--         CASCADE). It describes the run that cleared the row, and that run's
--         record is what retention removed.
--     It now takes the per-table counts the restore expects and returns them less
--     the promotions it deleted, so fn_ledger_snapshot_verify_restored still holds
--     the ledger to exactly the rows the snapshot does, minus only those. The
--     signature changes, hence DROP + CREATE.
--   * fn_ledger_snapshot_restore (as migration 280 left it) and
--     fn_ledger_snapshot_restore_parts (as migration 247 left it) pass their
--     expected counts through the fix-up. No other statement in either body changes.
--
-- DDL ONLY. No data is read, written or moved: the ALTER CONSTRAINTs change the
-- catalog and validate nothing, and the functions run only on a restore.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. The three references to ledger_operations a snapshot carries.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers
    ALTER CONSTRAINT txn_headers_ledger_operation_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE ledger_operation_promotions
    ALTER CONSTRAINT ledger_operation_promotions_ledger_operation_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE ledger_operation_promotions
    ALTER CONSTRAINT ledger_operation_promotions_run_ledger_fkey DEFERRABLE INITIALLY DEFERRED;

-- ---------------------------------------------------------------------------
-- 2. The outward-reference fix-up (migration 247), now also for pruned runs.
-- ---------------------------------------------------------------------------
DROP FUNCTION fn_ledger_snapshot_reconcile_external_refs(uuid);

-- Applies to restored rows the ON DELETE action their FK declares, for parents
-- that were deleted after the snapshot was taken. Runs after every insert and
-- before the deferred checks fire. One statement per outward FK whose parent an
-- app path can delete; SchemaDriftGuardTests holds the list of the others.
-- p_expected is the restore's table -> row count; the result is the same map less
-- the rows this function deleted, for fn_ledger_snapshot_verify_restored.
CREATE FUNCTION fn_ledger_snapshot_reconcile_external_refs(p_ledger_id uuid, p_expected jsonb)
RETURNS jsonb
LANGUAGE plpgsql
AS $$
DECLARE
    v_dropped bigint;
BEGIN
    -- accounts → feed_connections: ON DELETE SET NULL (feed_connection_id).
    -- Deleting a bank connection unlinks its accounts; restoring a snapshot
    -- taken before the delete must leave them unlinked, not fail.
    UPDATE accounts a
       SET feed_connection_id = NULL
     WHERE a.ledger_id = p_ledger_id
       AND a.feed_connection_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM feed_connections f
                        WHERE f.id = a.feed_connection_id
                          AND f.ledger_id = a.ledger_id);

    -- txn_headers → ledger_operations: ON DELETE SET NULL (ledger_operation_id).
    -- Audit retention prunes runs past its window, which unstamps the rows they
    -- wrote; a restored row whose run has gone is unstamped the same way.
    UPDATE txn_headers h
       SET ledger_operation_id = NULL
     WHERE h.ledger_id = p_ledger_id
       AND h.ledger_operation_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM ledger_operations o
                        WHERE o.id = h.ledger_operation_id);

    -- ledger_operation_promotions → ledger_operations: ON DELETE CASCADE, on both
    -- the run FK and the composite (run, ledger) FK. Audit retention deletes a
    -- run's promotions with it; a restored promotion whose run has gone goes too.
    DELETE FROM ledger_operation_promotions p
     WHERE p.ledger_id = p_ledger_id
       AND NOT EXISTS (SELECT 1 FROM ledger_operations o
                        WHERE o.id = p.ledger_operation_id
                          AND o.ledger_id = p.ledger_id);
    GET DIAGNOSTICS v_dropped = ROW_COUNT;

    RETURN p_expected || jsonb_build_object(
        'ledger_operation_promotions',
        COALESCE((p_expected ->> 'ledger_operation_promotions')::bigint, 0) - v_dropped);
END;
$$;

-- ---------------------------------------------------------------------------
-- 3. Both restore paths pass their counts through the fix-up.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_restore(p_ledger_id uuid, p_payload text)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_payload  jsonb := p_payload::jsonb;
    v_expected jsonb;
BEGIN
    PERFORM fn_snapshot_require_consistent_read('fn_ledger_snapshot_restore');

    SELECT COALESCE(jsonb_object_agg(t, jsonb_array_length(COALESCE(v_payload -> t, '[]'::jsonb))), '{}'::jsonb)
      INTO v_expected
      FROM unnest(fn_ledger_snapshot_part_names()) AS t;

    PERFORM fn_ledger_snapshot_clear(p_ledger_id);

    -- ----- Insert rows from the payload (forward-FK order) --------------
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    INSERT INTO budget_targets             SELECT * FROM jsonb_populate_recordset(NULL::budget_targets,             v_payload->'budget_targets');
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO security_components        SELECT * FROM jsonb_populate_recordset(NULL::security_components,        v_payload->'security_components');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO security_splits            SELECT * FROM jsonb_populate_recordset(NULL::security_splits,            v_payload->'security_splits');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO account_folders            SELECT * FROM jsonb_populate_recordset(NULL::account_folders,            v_payload->'account_folders');
    INSERT INTO account_folder_members     SELECT * FROM jsonb_populate_recordset(NULL::account_folder_members,     v_payload->'account_folder_members');
    INSERT INTO provider_security_mappings SELECT * FROM jsonb_populate_recordset(NULL::provider_security_mappings, v_payload->'provider_security_mappings');
    INSERT INTO recurring_transactions     SELECT * FROM jsonb_populate_recordset(NULL::recurring_transactions,     v_payload->'recurring_transactions');
    INSERT INTO txn_headers                SELECT * FROM jsonb_populate_recordset(NULL::txn_headers,                v_payload->'txn_headers');
    INSERT INTO txn_legs                   SELECT * FROM jsonb_populate_recordset(NULL::txn_legs,                   v_payload->'txn_legs');
    INSERT INTO lots                       SELECT * FROM jsonb_populate_recordset(NULL::lots,                       v_payload->'lots');
    INSERT INTO txn_leg_recon              SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_recon,              v_payload->'txn_leg_recon');
    INSERT INTO realized_gains             SELECT * FROM jsonb_populate_recordset(NULL::realized_gains,             v_payload->'realized_gains');
    INSERT INTO recurring_occurrence_exceptions SELECT * FROM jsonb_populate_recordset(NULL::recurring_occurrence_exceptions, v_payload->'recurring_occurrence_exceptions');
    INSERT INTO txn_header_originals       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_originals,       v_payload->'txn_header_originals');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');
    INSERT INTO txn_leg_tags               SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_tags,               v_payload->'txn_leg_tags');
    INSERT INTO txn_leg_note_tags          SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_note_tags,          v_payload->'txn_leg_note_tags');
    INSERT INTO saved_reports              SELECT * FROM jsonb_populate_recordset(NULL::saved_reports,              v_payload->'saved_reports');
    INSERT INTO report_pins                SELECT * FROM jsonb_populate_recordset(NULL::report_pins,                v_payload->'report_pins');
    INSERT INTO ledger_operation_promotions SELECT * FROM jsonb_populate_recordset(NULL::ledger_operation_promotions, v_payload->'ledger_operation_promotions');
    INSERT INTO ledger_benchmarks          SELECT * FROM jsonb_populate_recordset(NULL::ledger_benchmarks,          v_payload->'ledger_benchmarks');
    INSERT INTO benchmark_growth           SELECT * FROM jsonb_populate_recordset(NULL::benchmark_growth,           v_payload->'benchmark_growth');
    INSERT INTO benchmark_coverage         SELECT * FROM jsonb_populate_recordset(NULL::benchmark_coverage,         v_payload->'benchmark_coverage');
    INSERT INTO fx_rates                   SELECT * FROM jsonb_populate_recordset(NULL::fx_rates,                   v_payload->'fx_rates');
    INSERT INTO fx_pair_coverage           SELECT * FROM jsonb_populate_recordset(NULL::fx_pair_coverage,           v_payload->'fx_pair_coverage');

    v_expected := fn_ledger_snapshot_reconcile_external_refs(p_ledger_id, v_expected);
    -- Fire the deferred checks here, where a violation names this function,
    -- rather than at the caller's COMMIT.
    SET CONSTRAINTS ALL IMMEDIATE;
    PERFORM fn_ledger_snapshot_verify_restored(p_ledger_id, v_expected);

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$function$;

CREATE OR REPLACE FUNCTION fn_ledger_snapshot_restore_parts(
    p_ledger_id   uuid,
    p_snapshot_id uuid
)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_table    text;
    v_expected jsonb := '{}'::jsonb;
BEGIN
    PERFORM fn_snapshot_require_consistent_read('fn_ledger_snapshot_restore_parts');

    -- The v3 form of mig 188's assertion. Capture always writes a seq=0 part for
    -- every table, so an absent realized_gains part means a payload captured
    -- before mig 188 or a partially written snapshot — either way, not restorable.
    PERFORM 1 FROM ledger_snapshot_parts
      WHERE snapshot_id = p_snapshot_id AND part_name = 'realized_gains';
    IF NOT FOUND THEN
        RAISE EXCEPTION
            'snapshot % has no realized_gains part; restore would leave realized '
            'gains empty. Recapture the snapshot.', p_snapshot_id;
    END IF;

    PERFORM fn_ledger_snapshot_clear(p_ledger_id);

    -- Chunks of one table are separate INSERTs. A reference to a row in a later
    -- chunk of the same table is legal only because every such FK is INITIALLY
    -- DEFERRED (mig 247; SchemaDriftGuardTests fails on one that is not).
    FOREACH v_table IN ARRAY fn_ledger_snapshot_insert_order() LOOP
        v_expected := v_expected
            || jsonb_build_object(v_table, fn_snapshot_restore_part(p_snapshot_id, v_table));
    END LOOP;

    v_expected := fn_ledger_snapshot_reconcile_external_refs(p_ledger_id, v_expected);
    SET CONSTRAINTS ALL IMMEDIATE;
    PERFORM fn_ledger_snapshot_verify_restored(p_ledger_id, v_expected);

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$$;

COMMIT;
