-- =============================================================================
-- 193 — chunked snapshot payload (v3): bound capture AND restore memory
-- =============================================================================
--
-- Mig 179 moved the snapshot payload server-side to fix an OOM in the API
-- process. It fixed that OOM and created a smaller one in Postgres: the whole
-- payload is still built as a single value, just on the other side of the wire.
--
-- Measured on prod 2026-08-13: a snapshot whose artifact is 184.2 MB peaked at
-- 2.49 GB of *anonymous* backend memory (2671382528 bytes, sampled at 1s from the
-- cgroup's memory.stat) — roughly 14x amplification. Against the container's 1g
-- mem_limit the kernel OOM-killed the backend (signal 9, CONSTRAINT_MEMCG); the
-- postmaster then terminated every other backend and entered crash recovery. The
-- scheduler could not persist next_run_at over the killed connection, so the job
-- stayed due and retried on the next tick — a daily job became a 15-minute crash
-- loop that ran ~2 days, taking the nightly whole-DB backup down with it.
--
-- Where the 14x comes from, all live simultaneously:
--   1. fn_ledger_snapshot_payload builds ONE jsonb_build_object over 21 jsonb_agg
--      subqueries — every table's full contents materialised at once, then a
--      second copy for the combined object.
--   2. mig 179 assigned that result into a plpgsql variable (another copy),
--   3. then evaluated octet_length(v_json::text) — a complete text rendering of
--      the whole document, allocated purely to count bytes,
--   4. then passed the variable to UPDATE.
--
-- Restore was the same shape in reverse: ledger_snapshot_restore_stored read
-- content_json, cast it to text, and handed it to fn_ledger_snapshot_restore,
-- which cast it back to jsonb — three full copies of the document to recover a
-- ledger. That is the path you need when things have already gone wrong, so it
-- gets the same treatment here rather than being left for later.
--
-- Fix: never hold the whole document, in either direction. Rows are captured per
-- table in chunks of p_chunk_rows into `ledger_snapshot_parts`, and restored the
-- same way — one chunk in flight at a time. Peak memory becomes a function of
-- chunk size and row width, flat as the ledger grows, which is the property mig
-- 179 did not have. Size accounting sums per-chunk octet_length, so the text
-- rendering is bounded by chunk too.
--
-- Chunking key: `ctid`, not the primary key. Two in-scope tables have no `id`
-- column (txn_header_tags PK (header_id, tag_id), user_account_group_members PK
-- (group_id, account_id)), so a keyset over `id` is not universal; ctid is, needs
-- no catalog lookup, and PG14+ serves `ctid > $x ORDER BY ctid` with a TID range
-- scan. Row movement under us is not a concern: the capture runs inside one
-- transaction, so VACUUM cannot relocate tuples visible to its snapshot, and the
-- rewriting commands that could (VACUUM FULL / CLUSTER) take AccessExclusiveLock.
--
-- FK ordering is now defined ONCE. The delete block that mig 181/188 carried
-- inline is extracted to fn_ledger_snapshot_clear and called by both the v2 and
-- v3 restore paths, so the two cannot drift. fn_ledger_snapshot_restore is
-- otherwise unchanged — same assertion, same insert order, same tail.
--
-- Compatibility: three formats coexist, gated in ledger_snapshot_restore_stored.
--   v1  content IS NOT NULL, content_json NULL      -> legacy gzip, API-side path
--   v2  content_json IS NOT NULL                    -> mig 179 single document
--   v3  rows in ledger_snapshot_parts               -> this migration
-- Existing v2 snapshots keep restoring through the unchanged v2 branch; nothing
-- is rewritten in place.
--
-- Function signatures for ledger_snapshot_write and ledger_snapshot_restore_stored
-- are unchanged, so the EF HasDbFunction bindings in AppDbContext need no edit.
-- =============================================================================

-- ----- Part storage ----------------------------------------------------------
-- One row per (snapshot, table, chunk). `content` is always a jsonb ARRAY, so a
-- part with zero rows is '[]' rather than absent — see the seq=0 note in the
-- writer below, which the restore assertion in mig 188 depends on.
CREATE TABLE ledger_snapshot_parts (
    snapshot_id uuid    NOT NULL REFERENCES ledger_snapshots(id) ON DELETE CASCADE,
    part_name   text    NOT NULL,
    seq         integer NOT NULL,
    content     jsonb   NOT NULL,
    PRIMARY KEY (snapshot_id, part_name, seq)
);

COMMENT ON TABLE ledger_snapshot_parts IS
    'mig 193: chunked v3 snapshot payload. One row per (snapshot, table, chunk); '
    'content is a jsonb array of that chunk''s rows. Presence of rows for a snapshot '
    'is the v3 format gate. No RLS, matching ledger_snapshots — see the note below.';

-- Access mirrors ledger_snapshots exactly: coffer_app CRUD (the capture and
-- restore paths run request-side), coffer_service everything.
--
-- NOTE — no RLS, and that is inherited rather than chosen. ledger_snapshots has
-- no RLS either (53 tables in this schema enable it; that one does not), so this
-- table follows the table it belongs to rather than introducing a second, subtly
-- different posture for the same data. It is worth recording plainly that both
-- tables hold a full copy of every row of a ledger — the same rows their source
-- tables protect with RLS — and are gated only by the API's LedgerAuthorizer.
-- Adding RLS to both is tracked as a follow-up; it is a behaviour change to an
-- existing table and does not belong in a memory fix.
GRANT SELECT, INSERT, UPDATE, DELETE ON ledger_snapshot_parts TO coffer_app;
GRANT ALL ON ledger_snapshot_parts TO coffer_service;

-- =============================================================================
-- Capture
-- =============================================================================

-- Captures one table's rows for a ledger into ledger_snapshot_parts, chunked, and
-- returns the summed uncompressed byte size of the chunks written. Peak memory is
-- one chunk's jsonb array plus its text rendering — independent of table size.
CREATE OR REPLACE FUNCTION fn_snapshot_write_part(
    p_snapshot_id uuid,
    p_ledger_id   uuid,
    p_table       text,
    p_chunk_rows  integer
)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_last    tid     := '(0,0)';
    v_seq     integer := 0;
    v_rows    integer;
    v_maxctid tid;
    v_chunk   jsonb;
    v_bytes   bigint  := 0;
BEGIN
    LOOP
        EXECUTE format(
            'WITH c AS (
                 SELECT t.ctid AS ct, to_jsonb(t) AS j
                   FROM %I t
                  WHERE t.ledger_id = $1
                    AND t.ctid > $2
                  ORDER BY t.ctid
                  LIMIT $3
             )
             SELECT COALESCE(jsonb_agg(j ORDER BY ct), ''[]''::jsonb),
                    max(ct),
                    count(*)::integer
               FROM c', p_table)
        INTO v_chunk, v_maxctid, v_rows
        USING p_ledger_id, v_last, p_chunk_rows;

        -- Always write seq=0, even for an empty table. fn_ledger_snapshot_restore
        -- (mig 188) RAISEs when a payload key is absent, and distinguishes a
        -- missing key from an empty '[]'. Skipping the write here would drop the
        -- key for any ledger with an empty in-scope table.
        EXIT WHEN v_rows = 0 AND v_seq > 0;

        INSERT INTO ledger_snapshot_parts (snapshot_id, part_name, seq, content)
        VALUES (p_snapshot_id, p_table, v_seq, v_chunk);

        v_bytes := v_bytes + octet_length(v_chunk::text);
        v_seq   := v_seq + 1;

        EXIT WHEN v_rows < p_chunk_rows;
        v_last := v_maxctid;
    END LOOP;

    RETURN v_bytes;
END;
$$;

-- The in-scope table list, in the order fn_ledger_snapshot_payload builds its
-- keys. Every key is also its table name. Kept as a function so capture, restore
-- and the round-trip test read one definition; a future migration adding a table
-- to the payload updates it here only.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_part_names()
RETURNS text[]
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT ARRAY[
        'accounts', 'securities', 'user_account_groups', 'account_external_ids',
        'security_prices', 'security_splits', 'holdings',
        'user_account_group_members', 'txn_headers', 'txn_legs', 'txn_leg_recon',
        'lots', 'realized_gains', 'txn_header_overrides', 'txn_leg_overrides',
        'tags', 'txn_header_tags', 'provider_security_mappings',
        'recurring_transactions', 'recurring_occurrence_exceptions', 'loan_terms'
    ];
$$;

-- v3 capture. Same signature and return shape as mig 179's version, so the EF
-- binding is untouched. content_json is left NULL: the payload now lives in the
-- parts table, and NULL content_json plus parts rows is what makes a snapshot v3.
CREATE OR REPLACE FUNCTION ledger_snapshot_write(
    p_snapshot_id uuid,
    p_ledger_id   uuid
)
RETURNS TABLE (content_size_uncompressed integer)
LANGUAGE plpgsql
AS $$
DECLARE
    -- 2000 rows/chunk keeps the widest in-scope row type well under ~10 MB per
    -- chunk. Lower it if a future table grows very wide; the cost of a smaller
    -- chunk is more statements, not more memory.
    v_chunk_rows CONSTANT integer := 2000;
    v_table  text;
    v_bytes  bigint := 0;
BEGIN
    DELETE FROM ledger_snapshot_parts WHERE snapshot_id = p_snapshot_id;

    FOREACH v_table IN ARRAY fn_ledger_snapshot_part_names() LOOP
        v_bytes := v_bytes + fn_snapshot_write_part(
            p_snapshot_id, p_ledger_id, v_table, v_chunk_rows);
    END LOOP;

    -- Sum of per-chunk renderings rather than one rendering of the whole
    -- document, so this runs a few bytes per chunk larger than the v2 number for
    -- the same data (array brackets and separators). It is a display figure for
    -- the SPA, not a checksum, and measuring it exactly is what cost 184 MB.
    UPDATE ledger_snapshots
       SET content_size_uncompressed = LEAST(v_bytes, 2147483647)::integer
     WHERE id = p_snapshot_id;

    RETURN QUERY SELECT LEAST(v_bytes, 2147483647)::integer;
END;
$$;

-- =============================================================================
-- Restore
-- =============================================================================

-- The delete half of a restore, extracted verbatim from mig 188's body so the v2
-- and v3 paths share one definition of reverse-FK order. Behaviour is unchanged;
-- the comments are mig 188's, kept because they explain the ordering constraints.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_clear(p_ledger_id uuid)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    -- loan_terms references accounts; must go before accounts.
    DELETE FROM loan_terms                 WHERE ledger_id = p_ledger_id;
    -- recurring_occurrence_exceptions references recurring_transactions; go first.
    DELETE FROM recurring_occurrence_exceptions WHERE ledger_id = p_ledger_id;
    -- recurring_transactions references accounts AND template headers (mig 183);
    -- must go before both accounts and txn_headers.
    DELETE FROM recurring_transactions     WHERE ledger_id = p_ledger_id;
    -- security_splits references securities; must go before securities.
    DELETE FROM security_splits            WHERE ledger_id = p_ledger_id;
    -- Children of txn_legs first: lots, realized gains, the recon overlay,
    -- override layers, tags.
    DELETE FROM lots                       WHERE ledger_id = p_ledger_id;
    DELETE FROM realized_gains             WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_recon              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_overrides          WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_overrides       WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags            WHERE ledger_id = p_ledger_id;
    -- Transaction graph.
    DELETE FROM txn_legs                   WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                WHERE ledger_id = p_ledger_id;
    -- Holdings / account-groups / per-security data.
    DELETE FROM user_account_group_members WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups        WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                   WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices            WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids       WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                       WHERE ledger_id = p_ledger_id;
    -- Roots last.
    DELETE FROM securities                 WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                   WHERE ledger_id = p_ledger_id;
    -- The materialised balance table.
    DELETE FROM txn_header_account_balances WHERE ledger_id = p_ledger_id;
END;
$$;

-- mig 188's restore, with the delete block replaced by the call above. The
-- assertion, insert order, balance rebuild and the reasoning for omitting the
-- FIFO recompute are all unchanged — see mig 188's header.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_restore(
    p_ledger_id uuid,
    p_payload   text
)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_payload jsonb := p_payload::jsonb;
BEGIN
    IF v_payload->'realized_gains' IS NULL THEN
        RAISE EXCEPTION
            'snapshot payload has no realized_gains key (pre-mig-188 payload?); '
            'restore would leave realized gains empty. Recapture the snapshot, or '
            'if the schema-version guard was relaxed, restore this payload with a '
            'recompute_holdings_cost_basis(%) pass instead.', p_ledger_id;
    END IF;

    PERFORM fn_ledger_snapshot_clear(p_ledger_id);

    -- ----- Insert rows from the payload (forward-FK order) --------------
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO security_splits            SELECT * FROM jsonb_populate_recordset(NULL::security_splits,            v_payload->'security_splits');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO user_account_groups        SELECT * FROM jsonb_populate_recordset(NULL::user_account_groups,        v_payload->'user_account_groups');
    INSERT INTO user_account_group_members SELECT * FROM jsonb_populate_recordset(NULL::user_account_group_members, v_payload->'user_account_group_members');
    INSERT INTO provider_security_mappings SELECT * FROM jsonb_populate_recordset(NULL::provider_security_mappings, v_payload->'provider_security_mappings');
    INSERT INTO recurring_transactions     SELECT * FROM jsonb_populate_recordset(NULL::recurring_transactions,     v_payload->'recurring_transactions');
    INSERT INTO txn_headers                SELECT * FROM jsonb_populate_recordset(NULL::txn_headers,                v_payload->'txn_headers');
    INSERT INTO txn_legs                   SELECT * FROM jsonb_populate_recordset(NULL::txn_legs,                   v_payload->'txn_legs');
    INSERT INTO lots                       SELECT * FROM jsonb_populate_recordset(NULL::lots,                       v_payload->'lots');
    INSERT INTO txn_leg_recon              SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_recon,              v_payload->'txn_leg_recon');
    INSERT INTO realized_gains             SELECT * FROM jsonb_populate_recordset(NULL::realized_gains,             v_payload->'realized_gains');
    INSERT INTO recurring_occurrence_exceptions SELECT * FROM jsonb_populate_recordset(NULL::recurring_occurrence_exceptions, v_payload->'recurring_occurrence_exceptions');
    INSERT INTO txn_header_overrides       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_overrides,       v_payload->'txn_header_overrides');
    INSERT INTO txn_leg_overrides          SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_overrides,          v_payload->'txn_leg_overrides');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$$;

-- Replays one table's chunks, in seq order, one chunk in flight at a time. This
-- is the streaming counterpart of fn_snapshot_write_part.
CREATE OR REPLACE FUNCTION fn_snapshot_restore_part(
    p_snapshot_id uuid,
    p_table       text
)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_chunk jsonb;
BEGIN
    FOR v_chunk IN
        SELECT content
          FROM ledger_snapshot_parts
         WHERE snapshot_id = p_snapshot_id
           AND part_name   = p_table
         ORDER BY seq
    LOOP
        EXECUTE format(
            'INSERT INTO %I SELECT * FROM jsonb_populate_recordset(NULL::%I, $1)',
            p_table, p_table)
        USING v_chunk;
    END LOOP;
END;
$$;

-- The same table set as fn_ledger_snapshot_part_names(), in forward-FK order for
-- inserts (mig 188's order) rather than capture order. It has to be a second list
-- — capture order is not insert-safe — but it is a *function* so the two can be
-- compared: SnapshotsTests asserts they hold the same set, which is what stops a
-- table being added to the payload and silently never restored.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_insert_order()
RETURNS text[]
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT ARRAY[
        'accounts', 'loan_terms', 'securities', 'tags',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'user_account_groups', 'user_account_group_members',
        'provider_security_mappings', 'recurring_transactions',
        'txn_headers', 'txn_legs', 'lots', 'txn_leg_recon', 'realized_gains',
        'recurring_occurrence_exceptions',
        'txn_header_overrides', 'txn_leg_overrides', 'txn_header_tags'
    ];
$$;

-- v3 restore: same semantics as fn_ledger_snapshot_restore, but the payload is
-- never assembled.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_restore_parts(
    p_ledger_id   uuid,
    p_snapshot_id uuid
)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_table text;
BEGIN
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

    FOREACH v_table IN ARRAY fn_ledger_snapshot_insert_order() LOOP
        PERFORM fn_snapshot_restore_part(p_snapshot_id, v_table);
    END LOOP;

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$$;

-- Reassembles a v3 payload into the single document v2 stored. NOT on the restore
-- path — it defeats the point of chunking — and exists for the round-trip test
-- and for diagnostics. Callers should expect it to allocate the full payload.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_parts_payload(p_snapshot_id uuid)
RETURNS jsonb
LANGUAGE sql
STABLE
AS $$
    SELECT jsonb_object_agg(part_name, rows_arr)
      FROM (
            SELECT p.part_name,
                   COALESCE(jsonb_agg(e.v ORDER BY p.seq, e.ord), '[]'::jsonb) AS rows_arr
              FROM ledger_snapshot_parts p
              LEFT JOIN LATERAL jsonb_array_elements(p.content)
                        WITH ORDINALITY AS e(v, ord) ON true
             WHERE p.snapshot_id = p_snapshot_id
             GROUP BY p.part_name
           ) s;
$$;

-- Signature unchanged. v3 (parts present) streams; v2 (content_json set) takes
-- the mig 179 path verbatim; anything else is a v1 gzip snapshot the API restores
-- itself and must never reach here.
CREATE OR REPLACE FUNCTION ledger_snapshot_restore_stored(
    p_snapshot_id uuid,
    p_ledger_id   uuid
)
RETURNS TABLE (ledger_id uuid)
LANGUAGE plpgsql
AS $$
DECLARE
    v_json      jsonb;
    v_has_parts boolean;
BEGIN
    -- Ownership is re-checked here rather than assumed from the caller: this
    -- function is reachable by coffer_app and ledger_snapshots carries no RLS.
    PERFORM 1 FROM ledger_snapshots s
      WHERE s.id = p_snapshot_id AND s.ledger_id = p_ledger_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'snapshot % does not belong to ledger %',
            p_snapshot_id, p_ledger_id;
    END IF;

    SELECT EXISTS (SELECT 1 FROM ledger_snapshot_parts p
                    WHERE p.snapshot_id = p_snapshot_id)
      INTO v_has_parts;

    IF v_has_parts THEN
        PERFORM fn_ledger_snapshot_restore_parts(p_ledger_id, p_snapshot_id);
        RETURN QUERY SELECT p_ledger_id;
        RETURN;
    END IF;

    SELECT s.content_json INTO v_json
      FROM ledger_snapshots s
     WHERE s.id = p_snapshot_id AND s.ledger_id = p_ledger_id;

    IF v_json IS NULL THEN
        RAISE EXCEPTION
            'snapshot % has neither v3 parts nor v2 content_json (legacy v1 snapshot?)',
            p_snapshot_id;
    END IF;

    PERFORM fn_ledger_snapshot_restore(p_ledger_id, v_json::text);
    RETURN QUERY SELECT p_ledger_id;
END;
$$;

-- Execution privileges mirror the mig 111 pattern.
GRANT EXECUTE ON FUNCTION fn_snapshot_write_part(uuid, uuid, text, integer) TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_snapshot_restore_part(uuid, text) TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_ledger_snapshot_part_names() TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_ledger_snapshot_insert_order() TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_ledger_snapshot_clear(uuid) TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_ledger_snapshot_restore_parts(uuid, uuid) TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_ledger_snapshot_parts_payload(uuid) TO coffer_app;
