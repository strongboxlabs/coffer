-- ---------------------------------------------------------------------------
-- 247 — snapshot restore: chunk-safe self-references, security_components,
--       and references to rows a restore does not bring back
--
-- Found by a failed production restore (2026-10-09):
--
--   ERROR: insert or update on table "txn_headers" violates foreign key
--          constraint "txn_headers_is_merged_into_ledger_fkey"
--   CONTEXT: fn_snapshot_restore_part(uuid,text) ... INSERT INTO txn_headers
--
-- Three defects, one family: the restore re-inserts rows whose references the
-- schema can only satisfy in an order the restore does not use.
--
-- 1. SELF-REFERENCES ACROSS A CHUNK BOUNDARY. Since mig 193 a v3 restore
--    replays each table in chunks of up to 2000 rows, keyset-ordered on id, one
--    INSERT per chunk. A non-deferrable FK is checked at the end of each
--    statement, so a row referencing a row of its own table that sits in a
--    LATER chunk fails. Three FKs are exposed:
--      txn_headers.is_merged_into     — a merged header points at its winner
--      accounts.parent_id             — a category points at its parent
--      accounts.holdings_account_id   — a brokerage points at its holdings sibling
--    Which rows cross is decided by random uuids, so any ledger past 2000
--    headers with a merge was a coin flip per merge — and once one crossing
--    exists, EVERY snapshot of that ledger is unrestorable. Small ledgers fit in
--    one chunk, which is why every test passed.
--
--    Fix: DEFERRABLE INITIALLY DEFERRED, mig 124's answer for the same problem
--    (recurring_transactions.template_header_id, the mutual reference with
--    txn_headers). Normal writes are single statements or EF SaveChanges in an
--    implicit transaction, where deferred and immediate checks fire at the same
--    call; nothing in the API maps a 23503 to a business error. The restore
--    functions end with SET CONSTRAINTS ALL IMMEDIATE, so a violation raises
--    INSIDE the restore — with its context — instead of at the caller's COMMIT.
--
-- 2. security_components WAS DESTROYED BY EVERY RESTORE. It has no ledger_id —
--    it composed through securities (mig 150) — so the completeness guard,
--    which recognised a ledger-scoped table by its ledger_id column, never saw
--    it. It was neither captured nor excluded: the restore's DELETE FROM
--    securities cascaded through it and nothing put the rows back. Every
--    restore silently erased the asset-class / region look-through of every
--    multi-asset fund.
--
--    Fix: denormalise ledger_id onto it exactly as mig 049 did for
--    security_prices (backfill, composite FK to securities(id, ledger_id)),
--    flatten its RLS to the mig 174 role-aware shape that a ledger_id makes
--    possible, and capture it. The guard now also follows foreign keys, so a
--    child table without a ledger_id can no longer hide.
--
-- 3. REFERENCES TO ROWS OUTSIDE THE SNAPSHOT. A captured row can point at a
--    table a restore does not bring back. accounts.feed_connection_id is the
--    one an ordinary action can break: take a snapshot, delete the bank
--    connection (the live accounts' reference is SET NULL), restore — and the
--    restored accounts point at a connection that no longer exists. The FK
--    already says what should happen to a row whose connection is deleted:
--    ON DELETE SET NULL (feed_connection_id). The restore now applies that same
--    action to a restored row whose connection is gone, instead of failing.
--    The FK is made deferrable so the insert can land before the fix-up runs.
--
--    The other outward references (to users, and txn_headers.ledger_operation_id)
--    have no app path that deletes their parent; SchemaDriftGuardTests
--    classifies each one and fails on any new one, so this is a recorded
--    assumption rather than a silent one.
--
-- 4. ledger_operation_promotions WAS DESTROYED BY EVERY RESTORE TOO. It was on
--    the guard's exclusion list as feed-import provenance that survives a
--    rollback, but its FK to txn_headers is ON DELETE CASCADE, so the restore's
--    DELETE FROM txn_headers took every row with it and nothing put them back:
--    afterwards each sync run read "0 promoted", and the pending amount each
--    promotion recorded — the only place it survives — was gone. It is now
--    CAPTURED, so it rolls back with the transactions it describes (decided
--    2026-10-09). Its parent ledger_operations stays outside the snapshot; no
--    app path deletes a run short of deleting its ledger, which the guard
--    records as an assumption rather than leaving implicit.
--
-- 5. A CAPTURE WAS NOT A POINT IN TIME. It ran at READ COMMITTED, where every
--    statement inside ledger_snapshot_write takes a fresh snapshot: a bank sync
--    committing between the txn_headers chunks and the txn_legs chunks produced
--    a snapshot holding legs whose header it did not hold — unrestorable, and
--    nothing would say so until the day it was needed. Capture and restore now
--    refuse to run below REPEATABLE READ (fn_snapshot_require_consistent_read),
--    and the API opens them at that level.
--
-- 6. NOTHING CHECKED THAT A CAPTURE OR A RESTORE WAS COMPLETE. Capture now
--    counts every row it writes per table and compares it with the live count in
--    the same snapshot; restore compares the ledger's rows per table afterwards
--    with the rows the snapshot holds. Either mismatch raises, which rolls the
--    whole operation back and reports it — a short snapshot is never listed, and
--    a restore that left rows behind (say, ones the delete could not see) or lost
--    some is never committed.
--
-- 7. Manual snapshots captured under the requesting user's RLS, so another
--    member's private saved reports, pins and account groups were invisible to
--    them — never captured, never restored — while the scheduled snapshot, which
--    runs as the service role, saw everything. The API now runs both through the
--    service role after its own owner check, so what a snapshot holds does not
--    depend on who asked for it. (No SQL change: the functions scope by ledger.)
--
-- ALSO: ledger_snapshot_write takes the chunk size as a parameter (default 2000,
-- the old constant), so a test can force every row into its own chunk. Without
-- that no test can reach defect 1 short of seeding 2000 headers.
--
-- Every existing snapshot is retired by this migration (ADR-0037: restore
-- refuses a cross-schema-version snapshot). That is the settled posture, and
-- the snapshots it retires were not restorable for this ledger anyway.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- 1. Deferrable self-references (and the outward reference fixed up in 3).
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers
    ALTER CONSTRAINT txn_headers_is_merged_into_ledger_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE accounts
    ALTER CONSTRAINT accounts_parent_id_ledger_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE accounts
    ALTER CONSTRAINT accounts_holdings_account_id_ledger_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE accounts
    ALTER CONSTRAINT accounts_feed_connection_id_ledger_fkey DEFERRABLE INITIALLY DEFERRED;

-- ---------------------------------------------------------------------------
-- 2. security_components gets a ledger_id (mig 049 pattern).
-- ---------------------------------------------------------------------------
ALTER TABLE security_components ADD COLUMN ledger_id UUID;

UPDATE security_components c
   SET ledger_id = s.ledger_id
  FROM securities s
 WHERE s.id = c.security_id;

ALTER TABLE security_components
    ALTER COLUMN ledger_id SET NOT NULL,
    ADD CONSTRAINT security_components_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT;

-- The composite reference is what makes ledger_id a derived fact rather than an
-- assertion: a component cannot claim a ledger its security is not in.
ALTER TABLE security_components
    DROP CONSTRAINT security_components_security_id_fkey,
    ADD CONSTRAINT security_components_security_id_fkey
        FOREIGN KEY (security_id, ledger_id) REFERENCES securities(id, ledger_id)
        ON DELETE CASCADE;

CREATE INDEX ix_security_components_ledger ON security_components (ledger_id);

-- RLS: the mig 174 role-aware pair, now on ledger_id directly instead of through
-- a securities sub-select (mig 174 kept the sub-select only because there was
-- no ledger_id to filter on).
DROP POLICY security_components_read  ON security_components;
DROP POLICY security_components_write ON security_components;

CREATE POLICY security_components_read ON security_components FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ledger_id FROM user_ledger_grants
                         WHERE user_id = current_app_user_id()));

CREATE POLICY security_components_write ON security_components FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ledger_id FROM user_ledger_grants
                         WHERE user_id = current_app_user_id()
                           AND role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ledger_id FROM user_ledger_grants
                              WHERE user_id = current_app_user_id()
                                AND role IN ('owner', 'editor')));

-- ---------------------------------------------------------------------------
-- 3. The outward-reference fix-up, shared by the v2 and v3 restore paths.
-- ---------------------------------------------------------------------------

-- Applies to restored rows the ON DELETE action their FK declares, for parents
-- that were deleted after the snapshot was taken. Runs after every insert and
-- before the deferred checks fire. One statement per outward FK whose parent an
-- app path can delete; SchemaDriftGuardTests holds the list of the others.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_reconcile_external_refs(p_ledger_id uuid)
RETURNS void
LANGUAGE plpgsql
AS $$
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
END;
$$;

-- ---------------------------------------------------------------------------
-- 3b. Consistency and completeness checks shared by capture and restore.
-- ---------------------------------------------------------------------------

-- Every statement of a capture or restore must see the same moment. READ
-- COMMITTED gives each statement its own, which is how a capture could hold a
-- leg without its header. Raised rather than silently upgraded: isolation is
-- fixed when the transaction starts, so only the caller can choose it.
CREATE OR REPLACE FUNCTION fn_snapshot_require_consistent_read(p_operation text)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF current_setting('transaction_isolation') NOT IN ('repeatable read', 'serializable') THEN
        RAISE EXCEPTION
            '% must run in a REPEATABLE READ or SERIALIZABLE transaction, not %: below that '
            'every statement sees a different moment, so the tables it reads need not agree '
            'with each other.', p_operation, current_setting('transaction_isolation');
    END IF;
END;
$$;

-- After a restore, the ledger holds exactly the rows the snapshot does, table by
-- table. p_expected maps table -> row count; an absent key means zero. Catches a
-- delete that missed rows (they survive beside the restored ones), an insert or
-- cascade that lost some, and a captured table the clear forgot.
CREATE OR REPLACE FUNCTION fn_ledger_snapshot_verify_restored(p_ledger_id uuid, p_expected jsonb)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_table text;
    v_live  bigint;
    v_want  bigint;
    v_bad   text := '';
BEGIN
    FOREACH v_table IN ARRAY fn_ledger_snapshot_part_names() LOOP
        EXECUTE format('SELECT count(*) FROM %I t WHERE t.ledger_id = $1', v_table)
          INTO v_live USING p_ledger_id;
        v_want := COALESCE((p_expected ->> v_table)::bigint, 0);
        IF v_live <> v_want THEN
            v_bad := v_bad || format(' %s: snapshot holds %s, ledger now has %s;', v_table, v_want, v_live);
        END IF;
    END LOOP;
    IF v_bad <> '' THEN
        RAISE EXCEPTION 'snapshot restore did not reproduce the snapshot —%', v_bad;
    END IF;
END;
$$;

-- mig 197's chunk writer, now counting what it writes and refusing to return
-- short. It also no longer skips the all-zero uuid: mig 197 started its keyset
-- strictly AFTER that sentinel, so a row whose id IS the all-zero uuid (an
-- importer can write explicit ids) was left out without a word. The first chunk
-- now starts AT it (>=), later chunks after the last id (>), so every row is
-- reachable; the count check is the net behind that, for any skip at all.
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
    -- this many rows the aggregate is tens of MB — far from the multi-GB peak
    -- that mig 193 set out to eliminate — but it fails rather than degrading
    -- quietly.
    c_unchunked_max CONSTANT bigint := 500000;

    v_has_id  boolean;
    v_count   bigint;
    v_written bigint  := 0;
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

    EXECUTE format('SELECT count(*) FROM %I t WHERE t.ledger_id = $1', p_table)
      INTO v_count USING p_ledger_id;

    IF NOT v_has_id THEN
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

        v_written := jsonb_array_length(v_chunk);
        v_bytes   := octet_length(v_chunk::text);
    ELSE
        LOOP
            -- First chunk from the all-zero uuid inclusive, then strictly after
            -- the last id written; both forms stay a plain index condition.
            EXECUTE format(
                'WITH c AS (
                     SELECT t.id AS k, to_jsonb(t) AS j
                       FROM %I t
                      WHERE t.ledger_id = $1
                        AND t.id %s $2
                      ORDER BY t.id
                      LIMIT $3
                 )
                 SELECT COALESCE(jsonb_agg(j ORDER BY k), ''[]''::jsonb),
                        count(*)::integer
                   FROM c', p_table, CASE WHEN v_seq = 0 THEN '>=' ELSE '>' END)
            INTO v_chunk, v_rows
            USING p_ledger_id, v_last, p_chunk_rows;

            EXIT WHEN v_rows = 0 AND v_seq > 0;

            INSERT INTO ledger_snapshot_parts (snapshot_id, part_name, seq, content)
            VALUES (p_snapshot_id, p_table, v_seq, v_chunk);

            v_written := v_written + v_rows;
            v_bytes   := v_bytes + octet_length(v_chunk::text);
            v_seq     := v_seq + 1;

            EXIT WHEN v_rows < p_chunk_rows;

            -- The next keyset bound is the last element of the chunk just
            -- ordered by id (Postgres 16 has no max(uuid)).
            v_last := (v_chunk -> -1 ->> 'id')::uuid;
        END LOOP;
    END IF;

    -- Same transaction snapshot as every read above (the caller guarantees
    -- REPEATABLE READ), so the two numbers can only differ if the capture
    -- dropped or duplicated rows.
    IF v_written <> v_count THEN
        RAISE EXCEPTION
            'snapshot capture of % wrote % rows but the ledger has % — refusing an incomplete snapshot',
            p_table, v_written, v_count;
    END IF;

    RETURN v_bytes;
END;
$$;

-- ---------------------------------------------------------------------------
-- 4. Capture: chunk size becomes a parameter. The signature changes, so DROP +
--    CREATE; the EF binding passes two arguments and resolves to the default.
-- ---------------------------------------------------------------------------
DROP FUNCTION ledger_snapshot_write(uuid, uuid);

CREATE FUNCTION ledger_snapshot_write(
    p_snapshot_id uuid,
    p_ledger_id   uuid,
    -- 2000 rows/chunk keeps the widest in-scope row type well under ~10 MB per
    -- chunk. Lower it if a future table grows very wide; the cost of a smaller
    -- chunk is more statements, not more memory. Tests pass 1 to put every row
    -- in its own chunk, which is the only way to prove a restore is
    -- order-independent within a table.
    p_chunk_rows  integer DEFAULT 2000
)
RETURNS TABLE (content_size_uncompressed integer)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table  text;
    v_bytes  bigint := 0;
BEGIN
    PERFORM fn_snapshot_require_consistent_read('ledger_snapshot_write');
    IF p_chunk_rows IS NULL OR p_chunk_rows < 1 THEN
        RAISE EXCEPTION 'ledger_snapshot_write: chunk size must be >= 1, got %', p_chunk_rows;
    END IF;

    DELETE FROM ledger_snapshot_parts WHERE snapshot_id = p_snapshot_id;

    FOREACH v_table IN ARRAY fn_ledger_snapshot_part_names() LOOP
        v_bytes := v_bytes + fn_snapshot_write_part(
            p_snapshot_id, p_ledger_id, v_table, p_chunk_rows);
    END LOOP;

    -- Sum of per-chunk renderings rather than one rendering of the whole
    -- document, so this runs a few bytes per chunk larger than the v2 number for
    -- the same data (array brackets and separators). It is a display figure for
    -- the SPA, not a checksum, and measuring it exactly is what cost a full extra
    -- text copy of the document.
    UPDATE ledger_snapshots
       SET content_size_uncompressed = LEAST(v_bytes, 2147483647)::integer
     WHERE id = p_snapshot_id;

    RETURN QUERY SELECT LEAST(v_bytes, 2147483647)::integer;
END;
$$;

GRANT EXECUTE ON FUNCTION ledger_snapshot_write(uuid, uuid, integer) TO coffer_app;

-- ---------------------------------------------------------------------------
-- 5. The snapshot / delete bodies, re-emitted with security_components and
--    ledger_operation_promotions added and the restore paths ending in the
--    fix-up and an immediate check. fn_ledger_delete needs no promotions line:
--    it deletes ledger_operations first, which cascades them.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_part_names()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'securities', 'security_components', 'user_account_groups',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'user_account_group_members', 'txn_headers', 'txn_legs', 'txn_leg_recon',
        'lots', 'realized_gains', 'txn_header_originals',
        'tags', 'txn_header_tags', 'txn_leg_tags', 'provider_security_mappings',
        'recurring_transactions', 'recurring_occurrence_exceptions', 'loan_terms',
        'budget_targets', 'saved_reports', 'report_pins', 'ledger_operation_promotions'
    ];
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_insert_order()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'loan_terms', 'budget_targets', 'securities',
        'security_components', 'tags',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'user_account_groups', 'user_account_group_members',
        'provider_security_mappings', 'recurring_transactions',
        'txn_headers', 'txn_legs', 'lots', 'txn_leg_recon', 'realized_gains',
        'recurring_occurrence_exceptions',
        'txn_header_originals', 'txn_header_tags', 'txn_leg_tags',
        'saved_reports', 'report_pins', 'ledger_operation_promotions'
    ];
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_payload(p_ledger_id uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 STABLE
AS $function$
DECLARE
    v_result jsonb;
BEGIN
    SELECT jsonb_build_object(
        'accounts',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM accounts t WHERE t.ledger_id = p_ledger_id),
        'securities',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM securities t WHERE t.ledger_id = p_ledger_id),
        'security_components',              (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_components t WHERE t.ledger_id = p_ledger_id),
        'user_account_groups',              (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_groups t WHERE t.ledger_id = p_ledger_id),
        'account_external_ids',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_external_ids t WHERE t.ledger_id = p_ledger_id),
        'security_prices',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_prices t WHERE t.ledger_id = p_ledger_id),
        'security_splits',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_splits t WHERE t.ledger_id = p_ledger_id),
        'holdings',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM holdings t WHERE t.ledger_id = p_ledger_id),
        'user_account_group_members',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_group_members t WHERE t.ledger_id = p_ledger_id),
        'txn_headers',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_headers t WHERE t.ledger_id = p_ledger_id),
        'txn_legs',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_legs t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_recon',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_recon t WHERE t.ledger_id = p_ledger_id),
        'lots',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        -- mig 188: realized_gains is derived, but re-deriving it on restore cost
        -- ~27s and overwrote the holdings/lots restored above. Keys on
        -- sell_leg_id (a captured txn_legs row) so it round-trips 1:1.
        'realized_gains',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM realized_gains t WHERE t.ledger_id = p_ledger_id),
        'txn_header_originals',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_originals t WHERE t.ledger_id = p_ledger_id),
        'tags',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_tags',                     (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_tags t WHERE t.ledger_id = p_ledger_id),
        'saved_reports',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM saved_reports t WHERE t.ledger_id = p_ledger_id),
        'report_pins',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM report_pins t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id),
        'recurring_transactions',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_transactions t WHERE t.ledger_id = p_ledger_id),
        'recurring_occurrence_exceptions',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_occurrence_exceptions t WHERE t.ledger_id = p_ledger_id),
        'loan_terms',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM loan_terms t WHERE t.ledger_id = p_ledger_id),
        'budget_targets',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM budget_targets t WHERE t.ledger_id = p_ledger_id),
        'ledger_operation_promotions',      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM ledger_operation_promotions t WHERE t.ledger_id = p_ledger_id)
    ) INTO v_result;
    RETURN v_result;
END;
$function$;

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
    INSERT INTO txn_header_originals       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_originals,       v_payload->'txn_header_originals');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');
    INSERT INTO txn_leg_tags               SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_tags,               v_payload->'txn_leg_tags');
    INSERT INTO saved_reports              SELECT * FROM jsonb_populate_recordset(NULL::saved_reports,              v_payload->'saved_reports');
    INSERT INTO report_pins                SELECT * FROM jsonb_populate_recordset(NULL::report_pins,                v_payload->'report_pins');
    INSERT INTO ledger_operation_promotions SELECT * FROM jsonb_populate_recordset(NULL::ledger_operation_promotions, v_payload->'ledger_operation_promotions');

    PERFORM fn_ledger_snapshot_reconcile_external_refs(p_ledger_id);
    -- Fire the deferred checks here, where a violation names this function,
    -- rather than at the caller's COMMIT.
    SET CONSTRAINTS ALL IMMEDIATE;
    PERFORM fn_ledger_snapshot_verify_restored(p_ledger_id, v_expected);

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$function$;

-- Replays one table's chunks in seq order and returns how many rows it
-- inserted, which the caller checks against the ledger afterwards. The return
-- type changes (void -> bigint), hence DROP + CREATE.
DROP FUNCTION fn_snapshot_restore_part(uuid, text);

CREATE FUNCTION fn_snapshot_restore_part(
    p_snapshot_id uuid,
    p_table       text
)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_chunk    jsonb;
    v_rows     bigint;
    v_inserted bigint := 0;
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
        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_inserted := v_inserted + v_rows;
    END LOOP;
    RETURN v_inserted;
END;
$$;

GRANT EXECUTE ON FUNCTION fn_snapshot_restore_part(uuid, text) TO coffer_app;

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
    -- chunk of the same table is legal only because every such FK is deferred
    -- (mig 247; SchemaDriftGuardTests fails on one that is not).
    FOREACH v_table IN ARRAY fn_ledger_snapshot_insert_order() LOOP
        v_expected := v_expected
            || jsonb_build_object(v_table, fn_snapshot_restore_part(p_snapshot_id, v_table));
    END LOOP;

    PERFORM fn_ledger_snapshot_reconcile_external_refs(p_ledger_id);
    SET CONSTRAINTS ALL IMMEDIATE;
    PERFORM fn_ledger_snapshot_verify_restored(p_ledger_id, v_expected);

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_clear(p_ledger_id uuid)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
BEGIN
    -- loan_terms references accounts; must go before accounts.
    DELETE FROM loan_terms                 WHERE ledger_id = p_ledger_id;
    -- budget_targets references accounts; must go before accounts.
    DELETE FROM budget_targets             WHERE ledger_id = p_ledger_id;
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
    DELETE FROM txn_header_originals       WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags            WHERE ledger_id = p_ledger_id;
    DELETE FROM report_pins                WHERE ledger_id = p_ledger_id;
    DELETE FROM saved_reports              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_tags               WHERE ledger_id = p_ledger_id;
    -- References txn_headers; captured since mig 247. Its parent run in
    -- ledger_operations is NOT cleared — the run log survives a rollback.
    DELETE FROM ledger_operation_promotions WHERE ledger_id = p_ledger_id;
    -- Transaction graph.
    DELETE FROM txn_legs                   WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                WHERE ledger_id = p_ledger_id;
    -- Holdings / account-groups / per-security data.
    DELETE FROM user_account_group_members WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups        WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                   WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices            WHERE ledger_id = p_ledger_id;
    DELETE FROM security_components        WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids       WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                       WHERE ledger_id = p_ledger_id;
    -- Roots last.
    DELETE FROM securities                 WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                   WHERE ledger_id = p_ledger_id;
    -- The materialised balance table.
    DELETE FROM txn_header_account_balances WHERE ledger_id = p_ledger_id;
END;
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_delete(p_ledger_id uuid)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
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
    DELETE FROM txn_header_originals         WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags              WHERE ledger_id = p_ledger_id;
    DELETE FROM report_pins                  WHERE ledger_id = p_ledger_id;
    DELETE FROM saved_reports                WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_tags                 WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_legs                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                  WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_group_members   WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups          WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                     WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices              WHERE ledger_id = p_ledger_id;
    DELETE FROM security_components          WHERE ledger_id = p_ledger_id;
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
$function$;

-- ---------------------------------------------------------------------------
-- 6. One snapshot create per ledger at a time.
--
-- The cap (five per ledger, auto-snaps evicted first) is checked before the
-- capture transaction, so two creates for one ledger could both see four and both
-- insert — six snapshots, the oldest hidden by the list's Take(5) and never
-- evicted. REPEATABLE READ does not help: a lock taken INSIDE the transaction
-- comes after the transaction's snapshot, so the count it guards is already
-- stale. The lock is therefore a SESSION advisory lock taken before the
-- transaction begins, and released after it ends; a session that dies releases
-- it with itself.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION snapshot_ledger_lock(p_ledger_id uuid)
RETURNS TABLE (locked boolean)
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_advisory_lock(hashtextextended('coffer.snapshot-create:' || p_ledger_id::text, 0));
    RETURN QUERY SELECT true;
END;
$$;

CREATE OR REPLACE FUNCTION snapshot_ledger_unlock(p_ledger_id uuid)
RETURNS TABLE (unlocked boolean)
LANGUAGE sql
AS $$
    SELECT pg_advisory_unlock(hashtextextended('coffer.snapshot-create:' || p_ledger_id::text, 0));
$$;

GRANT EXECUTE ON FUNCTION snapshot_ledger_lock(uuid) TO coffer_app, coffer_service;
GRANT EXECUTE ON FUNCTION snapshot_ledger_unlock(uuid) TO coffer_app, coffer_service;

