-- =============================================================================
-- 195 — fn_ledger_snapshot_parts_payload: empty parts must reassemble as [], not [null]
-- =============================================================================
--
-- Mig 193 added fn_ledger_snapshot_parts_payload to rebuild the single v2-shaped
-- document from a v3 snapshot's chunks. It aggregates chunk elements with
--
--     LEFT JOIN LATERAL jsonb_array_elements(p.content) WITH ORDINALITY AS e(v, ord)
--     ... COALESCE(jsonb_agg(e.v ORDER BY p.seq, e.ord), '[]'::jsonb)
--
-- For a part whose chunk is '[]' — which capture writes deliberately for every
-- in-scope table that is empty for the ledger — jsonb_array_elements returns no
-- rows, the LEFT JOIN supplies one row with e.v = NULL, and jsonb_agg aggregates
-- that NULL into a JSON null. The result is '[null]', not '[]'. The COALESCE never
-- fires, because jsonb_agg returned a value; it just wasn't the right one.
--
-- Downstream that is worse than an empty array, not equivalent to it:
-- jsonb_populate_recordset(NULL::accounts, '[null]') yields one all-NULL record,
-- so a restore fed this document tries to insert a row of NULLs and dies on the
-- first NOT NULL column.
--
-- IMPACT: none in production. The function is diagnostic-only — v3 restore streams
-- chunks through fn_snapshot_restore_part and never reassembles the document, so
-- nothing in the product called this. It was found by a test written to cover the
-- *other* untested thing in mig 193 (see below), which used this helper to
-- manufacture a v2 snapshot and got a 500 instead of a restore.
--
-- Fix: aggregate only non-NULL elements, so an empty part reassembles as '[]'.
--
-- The reason a test was reaching for this at all is worth recording: mig 193 made
-- capture produce v3, so every snapshot test exercises v3 and none exercises v2 —
-- while 193 also refactored the v2 restore function (extracting its delete block
-- into fn_ledger_snapshot_clear). An install upgrading to 193 keeps its existing
-- v2 snapshots and restores them through that refactored path. The companion test
-- A_pre_migration_v2_snapshot_still_restores closes that gap, and needs this fix
-- to be able to build its fixture.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_ledger_snapshot_parts_payload(p_snapshot_id uuid)
RETURNS jsonb
LANGUAGE sql
STABLE
AS $$
    SELECT jsonb_object_agg(part_name, rows_arr)
      FROM (
            SELECT p.part_name,
                   COALESCE(
                       jsonb_agg(e.v ORDER BY p.seq, e.ord) FILTER (WHERE e.v IS NOT NULL),
                       '[]'::jsonb) AS rows_arr
              FROM ledger_snapshot_parts p
              LEFT JOIN LATERAL jsonb_array_elements(p.content)
                        WITH ORDINALITY AS e(v, ord) ON true
             WHERE p.snapshot_id = p_snapshot_id
             GROUP BY p.part_name
           ) s;
$$;
