-- =============================================================================
-- 197 — chunk on the primary key, not ctid: the ctid keyset re-scanned per chunk
-- =============================================================================
--
-- CORRECTION to migration 193. Its header argued:
--
--   "ctid ... needs no catalog lookup, and PG14+ serves `ctid > $x ORDER BY ctid`
--    with a TID range scan."
--
-- That is true of a ctid **literal** and false of a bound **parameter**, which is
-- what fn_snapshot_write_part actually passes (EXECUTE ... USING). The planner
-- sees an opaque parameter, cannot judge selectivity, and never considers a TID
-- range scan. The mechanism was asserted from documentation instead of read off a
-- plan, and 193 shipped on that assumption.
--
-- What it really did, measured on prod (42,785 headers, 97 MB of jsonb):
--
--   Limit
--     -> Sort  (Sort Key: t.ctid)
--          Sort Method: external merge  Disk: 97328kB
--          -> Seq Scan on txn_headers
--               Filter: ((ctid > $2) AND (ledger_id = $1))
--               Rows Removed by Filter: 24
--     Execution Time: 676.249 ms
--
-- Every chunk sequentially scanned the whole table, built to_jsonb for every
-- matching row (to_jsonb sits above the Sort in the target list), spilled a 97 MB
-- sort to disk, and then discarded all but p_chunk_rows of it. 22 chunks for
-- txn_headers, 64 for txn_legs, ~165 in total: roughly 6 GB of jsonb constructed
-- and ~4 GB of temp files written to produce a 183 MB artifact. Capture took ~40s.
--
-- Note what this means about 193's headline result. Peak anon memory really was
-- 56 MiB (down from 2.49 GB), but part of that was Postgres paging the per-chunk
-- sort to disk rather than the chunking being frugal. The memory fix stands; it
-- was just also buying disk I/O nobody had measured.
--
-- The fix: keyset on the primary key, so an index supplies the order and LIMIT
-- stops early — to_jsonb is then evaluated only for rows actually returned. Same
-- query on the same data:
--
--   Limit
--     -> Index Scan using uq_txn_headers_id_ledger
--          Index Cond: ((id > $2) AND (ledger_id = $1))
--     Execution Time: 40.489 ms   (~31 ms excluding a subselect the function
--                                  does not pay — the ledger is a parameter)
--
-- No Sort, no temp files, ~20x faster per chunk. Both predicates are index
-- conditions because txn_headers carries a composite unique index on
-- (id, ledger_id); tables with only the `id` primary key still get an ordered
-- index scan with ledger_id as a cheap filter, which is what matters.
--
-- Tables without an `id` column: txn_header_tags (PK header_id, tag_id) and
-- user_account_group_members (PK group_id, account_id) — pure join tables of two
-- uuids, one part each in practice. Rather than build a generic composite-key
-- row-comparison keyset (variable parameter counts in EXECUTE ... USING, and a
-- third table list to keep in step with the two 193 already has), they are written
-- as a single part, guarded by a row-count ceiling that fails loudly rather than
-- silently reintroducing an unbounded allocation. The `id` test is a catalog
-- lookup, not a hardcoded name list.
--
-- VERIFIED IN THE STRESS LANE, which took two harness fixes to make possible —
-- and the first attempt concluded the opposite:
--
--   * The seeder wrote headers with six thin columns and no provider_raw_payload,
--     so rows were ~10x narrower than production's ~2.3 KB. The sort never spilled
--     and there was nothing to fix.
--   * Bulk-seeded tables were never ANALYZEd, so the planner worked from defaults
--     (22 estimated rows against 50,000 actual) and chose a bitmap scan plus an
--     external-merge sort for BOTH keysets. They measured within noise of each
--     other, which reads exactly like "this migration does nothing".
--
-- With realistic payload width and statistics present, the per-chunk plan matches
-- what was measured on prod — ctid: 555 ms with a 159 MB external merge; id: 39 ms
-- on an Index Scan, no sort. End to end, capture of a 50,000-transaction ledger
-- goes 18,289 ms -> 5,136 ms (back-to-back runs, only this migration differing).
--
-- No claim is made here about restore: those figures moved between runs for
-- reasons unrelated to this change (container warm-up), and a number that cannot
-- be attributed is not evidence.
--
-- Still not addressed: octet_length(chunk::text) is a full extra pass over the
-- payload for a display figure, and after this migration it is the largest
-- remaining cost in capture. Left alone here because changing it changes a number
-- the SPA shows, which is a product decision rather than a defect.
-- =============================================================================

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
    -- Ceiling for the unchunked path only. Two uuid columns per row, so even at
    -- this many rows the aggregate is tens of MB — far from the 2.49 GB that
    -- mig 193 set out to eliminate — but it fails rather than degrading quietly.
    c_unchunked_max CONSTANT bigint := 500000;

    v_has_id  boolean;
    v_count   bigint;
    v_last    uuid    := '00000000-0000-0000-0000-000000000000';
    v_seq     integer := 0;
    v_rows    integer;
    v_chunk   jsonb;
    v_bytes   bigint  := 0;
BEGIN
    SELECT EXISTS (
        SELECT 1
          FROM pg_attribute a
         WHERE a.attrelid = p_table::regclass
           AND a.attname  = 'id'
           AND a.attnum   > 0
           AND NOT a.attisdropped)
      INTO v_has_id;

    IF NOT v_has_id THEN
        EXECUTE format('SELECT count(*) FROM %I t WHERE t.ledger_id = $1', p_table)
          INTO v_count USING p_ledger_id;

        IF v_count > c_unchunked_max THEN
            RAISE EXCEPTION
                'snapshot part %: % rows and no id column to key a chunked scan on. '
                'Add a composite keyset for this table before it grows further; '
                'capturing it in one aggregate would reintroduce the mig 193 OOM.',
                p_table, v_count;
        END IF;

        EXECUTE format(
            'SELECT COALESCE(jsonb_agg(to_jsonb(t)), ''[]''::jsonb)
               FROM %I t WHERE t.ledger_id = $1', p_table)
          INTO v_chunk USING p_ledger_id;

        -- seq=0 is written even when empty: fn_ledger_snapshot_restore (mig 188)
        -- distinguishes an absent payload key from an empty '[]'.
        INSERT INTO ledger_snapshot_parts (snapshot_id, part_name, seq, content)
        VALUES (p_snapshot_id, p_table, 0, v_chunk);

        RETURN octet_length(v_chunk::text);
    END IF;

    LOOP
        -- The all-zero uuid is the opening sentinel. gen_random_uuid() cannot
        -- produce it (version and variant bits are fixed), so no real row is
        -- skipped, and `id > $2` stays a plain index condition — which a
        -- `($2 IS NULL OR ...)` form would not.
        EXECUTE format(
            'WITH c AS (
                 SELECT t.id AS k, to_jsonb(t) AS j
                   FROM %I t
                  WHERE t.ledger_id = $1
                    AND t.id > $2
                  ORDER BY t.id
                  LIMIT $3
             )
             SELECT COALESCE(jsonb_agg(j ORDER BY k), ''[]''::jsonb),
                    count(*)::integer
               FROM c', p_table)
        INTO v_chunk, v_rows
        USING p_ledger_id, v_last, p_chunk_rows;

        EXIT WHEN v_rows = 0 AND v_seq > 0;

        INSERT INTO ledger_snapshot_parts (snapshot_id, part_name, seq, content)
        VALUES (p_snapshot_id, p_table, v_seq, v_chunk);

        v_bytes := v_bytes + octet_length(v_chunk::text);
        v_seq   := v_seq + 1;

        EXIT WHEN v_rows < p_chunk_rows;

        -- The next keyset bound is the last element of the chunk we just ordered
        -- by id, so it needs no aggregate. Postgres 16 has no max(uuid) — the
        -- first cut of this migration called one and every capture failed with
        -- "function max(uuid) does not exist". Reached only on a full chunk, so
        -- v_chunk is guaranteed non-empty here.
        v_last := (v_chunk -> -1 ->> 'id')::uuid;
    END LOOP;

    RETURN v_bytes;
END;
$$;
