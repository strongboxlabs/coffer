-- =============================================================================
-- 179 — server-side snapshot payload (fix the OOM on large ledgers)
-- =============================================================================
--
-- ADR-0037 snapshots stored the in-scope graph as gzip-compressed JSON in
-- `content` (bytea). The create + restore paths round-tripped the whole payload
-- through the API process: read the jsonb → materialise a ~180 MB string →
-- JsonSerializer.Deserialize into an in-memory object graph (GBs) → re-serialise
-- → gzip. Under the API container's mem_limit this OOMs once a ledger's payload
-- gets large (observed: a 176 MB payload -> OutOfMemoryException, auto-snapshots
-- silently failing every night while the daily whole-DB BACKUP — pg_dump, streamed
-- — kept working).
--
-- Fix: keep the payload entirely server-side. A new `content_json` (jsonb) column
-- holds the in-scope graph captured by fn_ledger_snapshot_payload; Postgres TOAST-
-- compresses it on disk. Create writes it via INSERT-then-server-side-UPDATE;
-- restore reads it and reuses the existing restore body — the blob never enters
-- managed memory, so it is OOM-proof at any ledger size.
--
-- Compatibility: pre-existing gzip snapshots keep `content` set and `content_json`
-- NULL (format v1) and restore via the old path; new snapshots set `content_json`
-- (v2) and leave `content` empty. `content_json IS NOT NULL` is the v2 gate.
-- =============================================================================

ALTER TABLE ledger_snapshots
    ADD COLUMN content_json jsonb;

COMMENT ON COLUMN ledger_snapshots.content_json IS
    'ADR-0037/mig 179: server-side snapshot payload (in-scope graph as jsonb, TOAST-'
    'compressed). NON-NULL => v2 (server-side, OOM-proof). NULL => v1 legacy gzip in content.';

-- v2 capture: fill content_json + content_size_uncompressed for an already-inserted
-- snapshot row, entirely server-side. TVF wrapper (HasDbFunction) over the existing
-- fn_ledger_snapshot_payload so no payload crosses the API boundary.
CREATE OR REPLACE FUNCTION ledger_snapshot_write(
    p_snapshot_id uuid,
    p_ledger_id   uuid
)
RETURNS TABLE (content_size_uncompressed integer)
LANGUAGE plpgsql
AS $$
DECLARE
    v_json jsonb;
    v_size integer;
BEGIN
    v_json := fn_ledger_snapshot_payload(p_ledger_id);
    v_size := octet_length(v_json::text);   -- uncompressed byte size, for SPA display
    UPDATE ledger_snapshots
        SET content_json = v_json,
            content_size_uncompressed = v_size
        WHERE id = p_snapshot_id;
    RETURN QUERY SELECT v_size;
END;
$$;

-- v2 restore: read the stored content_json (the in-scope graph) and reuse the
-- existing fn_ledger_snapshot_restore body — all server-side. Caller has already
-- validated the schema version + ledger ownership.
CREATE OR REPLACE FUNCTION ledger_snapshot_restore_stored(
    p_snapshot_id uuid,
    p_ledger_id   uuid
)
RETURNS TABLE (ledger_id uuid)
LANGUAGE plpgsql
AS $$
DECLARE
    v_json jsonb;
BEGIN
    SELECT s.content_json INTO v_json
        FROM ledger_snapshots s
        WHERE s.id = p_snapshot_id AND s.ledger_id = p_ledger_id;
    IF v_json IS NULL THEN
        RAISE EXCEPTION 'snapshot % has no v2 content_json (legacy v1 snapshot?)', p_snapshot_id;
    END IF;
    PERFORM fn_ledger_snapshot_restore(p_ledger_id, v_json::text);
    RETURN QUERY SELECT p_ledger_id;
END;
$$;
