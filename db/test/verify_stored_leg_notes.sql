-- Verify the stored memo-and-tags presentation of migration 264: the notes_*
-- columns on txn_legs, the txn_leg_note_tags table, the tag-name-key index, the
-- ordered lock functions, the view's new columns and the snapshot capture.
--
-- NO SQL DEFINITION OF THE RULE EXISTS (264): the values are the C# rule's
-- output, written by LegNotes and the backfill step, and the C# tests check what
-- they hold. What the database itself guarantees is checked here:
--
--   * a leg's presentation is whole or absent, and its kind one of the six;
--   * a note-tag row names a leg and a tag of its own ledger, cascades from
--     both, and is one row per (leg, tag);
--   * members read the note tags, owners and editors write them, and nobody
--     updates one in place (a changed row is deleted and inserted again);
--   * one tag per name key per ledger, as tag_name_key (263) defines the key;
--   * the three lock functions refuse any isolation level but READ COMMITTED;
--   * resolved_transactions carries the row's leg's presentation and the
--     posting's other leg's;
--   * a snapshot captures txn_leg_note_tags after the legs and the tags, and the
--     writer chunks it on its composite primary key without losing or
--     repeating a row; a captured table without a primary key is refused.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_stored_leg_notes.sql
-- All assertions are plpgsql DO blocks; any failure aborts. Each transaction
-- ends in ROLLBACK.

-- ---------------------------------------------------------------------------
-- The lock functions, in a transaction that is not READ COMMITTED.
-- ---------------------------------------------------------------------------
BEGIN ISOLATION LEVEL REPEATABLE READ;

DO $$
DECLARE
    v_fn text;
BEGIN
    FOREACH v_fn IN ARRAY ARRAY['fn_lock_txn_headers', 'fn_lock_tags', 'fn_lock_accounts'] LOOP
        BEGIN
            EXECUTE format('SELECT * FROM %I($1)', v_fn) USING ARRAY[]::uuid[];
            RAISE EXCEPTION 'FAIL: % ran under REPEATABLE READ', v_fn;
        EXCEPTION WHEN invalid_transaction_state THEN NULL;
        END;
    END LOOP;
    RAISE NOTICE 'OK: the lock functions refuse REPEATABLE READ';
END $$;

ROLLBACK;

BEGIN;

DO $$
DECLARE
    v_ledger   uuid := gen_random_uuid();
    v_other    uuid := gen_random_uuid();
    v_owner    uuid := gen_random_uuid();
    v_editor   uuid := gen_random_uuid();
    v_viewer   uuid := gen_random_uuid();
    v_stranger uuid := gen_random_uuid();
    v_bank     uuid := gen_random_uuid();
    v_cat      uuid := gen_random_uuid();
    v_o_bank   uuid := gen_random_uuid();
    v_o_cat    uuid := gen_random_uuid();
    v_header   uuid := gen_random_uuid();
    v_o_header uuid := gen_random_uuid();
    v_leg_bank uuid := gen_random_uuid();
    v_leg_cat  uuid := gen_random_uuid();
    v_o_leg    uuid := gen_random_uuid();
    v_tag_a    uuid := gen_random_uuid();
    v_tag_b    uuid := gen_random_uuid();
    v_tag_c    uuid := gen_random_uuid();
    v_o_tag    uuid := gen_random_uuid();
    v_snap     uuid := gen_random_uuid();
    v_day      timestamptz := TIMESTAMPTZ '2026-03-04 12:00:00+00';
    v_n        integer;
    v_kind     text;
    v_cp_kind  text;
BEGIN
    INSERT INTO ledgers (id, name) VALUES (v_ledger, 'Notes Test'), (v_other, 'Notes Other');
    INSERT INTO users (id, display_name) VALUES
        (v_owner, 'owner'), (v_editor, 'editor'), (v_viewer, 'viewer'), (v_stranger, 'stranger');
    INSERT INTO user_ledger_grants (user_id, ledger_id, role) VALUES
        (v_owner, v_ledger, 'owner'), (v_editor, v_ledger, 'editor'),
        (v_viewer, v_ledger, 'viewer'), (v_stranger, v_other, 'owner');
    INSERT INTO accounts (id, ledger_id, name, account_type) VALUES
        (v_bank, v_ledger, 'bank', 'bank'), (v_o_bank, v_other, 'bank', 'bank');
    INSERT INTO accounts (id, ledger_id, name, account_type, category_kind) VALUES
        (v_cat, v_ledger, 'category', 'category', 'expense'),
        (v_o_cat, v_other, 'category', 'category', 'expense');
    INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, created_at,
                             originating_account_id) VALUES
        (v_header,   v_ledger, 'manual', 'payee', v_day, v_day, v_day, v_bank),
        (v_o_header, v_other,  'manual', 'payee', v_day, v_day, v_day, v_o_bank);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount) VALUES
        (v_leg_bank, v_header, v_ledger, v_bank, 0, -10),
        (v_leg_cat,  v_header, v_ledger, v_cat,  0,  10),
        (v_o_leg,    v_o_header, v_other, v_o_bank, 0, -10),
        (gen_random_uuid(), v_o_header, v_other, v_o_cat, 0, 10);
    INSERT INTO tags (id, ledger_id, name) VALUES
        (v_tag_a, v_ledger, 'Food'), (v_tag_b, v_ledger, 'Travel'), (v_tag_c, v_ledger, 'Home'),
        (v_o_tag, v_other, 'Food');

    -- ---- ck_txn_legs_notes_kind / ck_txn_legs_notes_whole -----------------
    BEGIN
        UPDATE txn_legs
           SET notes_kind = 'Unknown', notes_is_read_only = false, notes_groups_by_header = true,
               notes_memo_inherited = false, notes_tags_inherited = false, notes_header_in_play = false
         WHERE id = v_leg_bank;
        RAISE EXCEPTION 'FAIL: a leg took a presentation kind outside the six';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Half a presentation, either way round.
    BEGIN
        UPDATE txn_legs SET notes_kind = 'OnePosting' WHERE id = v_leg_bank;
        RAISE EXCEPTION 'FAIL: a leg took a kind without its flags';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE txn_legs SET notes_memo = 'memo' WHERE id = v_leg_bank;
        RAISE EXCEPTION 'FAIL: a leg took a memo with no presentation';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE txn_legs
           SET notes_kind = 'OnePosting', notes_is_read_only = false, notes_groups_by_header = true,
               notes_memo_inherited = false, notes_tags_inherited = false
         WHERE id = v_leg_bank;
        RAISE EXCEPTION 'FAIL: a leg took a presentation missing a flag';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Whole: the kind and every flag; the memo values may be NULL.
    UPDATE txn_legs
       SET notes_kind = 'OnePosting', notes_is_read_only = false, notes_groups_by_header = true,
           notes_memo_inherited = false, notes_tags_inherited = false, notes_header_in_play = false,
           notes_memo = 'own memo'
     WHERE id = v_leg_bank;
    UPDATE txn_legs
       SET notes_kind = 'OnePosting', notes_is_read_only = false, notes_groups_by_header = false,
           notes_memo_inherited = true, notes_tags_inherited = true, notes_header_in_play = false,
           notes_memo = 'own memo', notes_inherited_memo = 'own memo', notes_inherit_from_account_id = v_bank
     WHERE id = v_leg_cat;

    -- ---- resolved_transactions: the row's leg and the posting's other leg ----
    SELECT rt.notes_kind, rt.counterparty_notes_kind INTO v_kind, v_cp_kind
      FROM resolved_transactions rt WHERE rt.id = v_leg_bank;
    IF v_kind IS DISTINCT FROM 'OnePosting' OR v_cp_kind IS DISTINCT FROM 'OnePosting' THEN
        RAISE EXCEPTION 'FAIL: the view does not carry both legs'' presentation: % / %', v_kind, v_cp_kind;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM resolved_transactions rt
                    WHERE rt.id = v_leg_bank
                      AND rt.notes_groups_by_header AND NOT rt.counterparty_notes_groups_by_header
                      AND rt.counterparty_notes_memo_inherited
                      AND rt.counterparty_notes_inherit_from_account_id = v_bank) THEN
        RAISE EXCEPTION 'FAIL: the view''s counterparty columns are not the other leg''s';
    END IF;

    -- ---- txn_leg_note_tags --------------------------------------------------
    INSERT INTO txn_leg_note_tags (leg_id, tag_id, header_id, ledger_id, shown, in_play, inherited, transaction_level)
    VALUES (v_leg_bank, v_tag_a, v_header, v_ledger, true, true, false, false),
           (v_leg_bank, v_tag_b, v_header, v_ledger, true, true, false, false),
           (v_leg_cat,  v_tag_a, v_header, v_ledger, true, true, true,  false);

    BEGIN
        INSERT INTO txn_leg_note_tags (leg_id, tag_id, header_id, ledger_id, shown, in_play, inherited, transaction_level)
        VALUES (v_leg_bank, v_tag_a, v_header, v_ledger, false, false, false, true);
        RAISE EXCEPTION 'FAIL: a second row for one (leg, tag)';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    -- Another ledger's tag on this ledger's leg, and this ledger's leg under
    -- another ledger: both refused by the composite references.
    BEGIN
        INSERT INTO txn_leg_note_tags (leg_id, tag_id, header_id, ledger_id, shown, in_play, inherited, transaction_level)
        VALUES (v_leg_bank, v_o_tag, v_header, v_ledger, true, true, false, false);
        RAISE EXCEPTION 'FAIL: a note tag named another ledger''s tag';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO txn_leg_note_tags (leg_id, tag_id, header_id, ledger_id, shown, in_play, inherited, transaction_level)
        VALUES (v_leg_bank, v_o_tag, v_header, v_other, true, true, false, false);
        RAISE EXCEPTION 'FAIL: a note tag named another ledger''s leg';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- ---- RLS, as the request role --------------------------------------------
    PERFORM set_config('app.user_id', v_viewer::text, true);
    SET LOCAL ROLE coffer_app;
    SELECT count(*) INTO v_n FROM txn_leg_note_tags WHERE header_id = v_header;
    IF v_n <> 3 THEN
        RAISE EXCEPTION 'FAIL: a viewer reads % note tag(s) of the ledger, not 3', v_n;
    END IF;
    BEGIN
        INSERT INTO txn_leg_note_tags (leg_id, tag_id, header_id, ledger_id, shown, in_play, inherited, transaction_level)
        VALUES (v_leg_cat, v_tag_b, v_header, v_ledger, true, true, true, false);
        RAISE EXCEPTION 'FAIL: a viewer wrote a note tag';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    RESET ROLE;

    PERFORM set_config('app.user_id', v_stranger::text, true);
    SET LOCAL ROLE coffer_app;
    SELECT count(*) INTO v_n FROM txn_leg_note_tags WHERE header_id = v_header;
    IF v_n <> 0 THEN
        RAISE EXCEPTION 'FAIL: a user with no grant on the ledger reads % of its note tag(s)', v_n;
    END IF;
    RESET ROLE;

    PERFORM set_config('app.user_id', v_editor::text, true);
    SET LOCAL ROLE coffer_app;
    INSERT INTO txn_leg_note_tags (leg_id, tag_id, header_id, ledger_id, shown, in_play, inherited, transaction_level)
    VALUES (v_leg_cat, v_tag_b, v_header, v_ledger, true, true, true, false);
    BEGIN
        UPDATE txn_leg_note_tags SET in_play = false WHERE leg_id = v_leg_cat AND tag_id = v_tag_b;
        RAISE EXCEPTION 'FAIL: the request role updated a note tag in place';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    DELETE FROM txn_leg_note_tags WHERE leg_id = v_leg_cat AND tag_id = v_tag_b;
    RESET ROLE;
    PERFORM set_config('app.user_id', '', true);

    -- ---- uq_tags_name_key_per_ledger --------------------------------------
    BEGIN
        INSERT INTO tags (ledger_id, name) VALUES (v_ledger, U&'\00A0food ');
        RAISE EXCEPTION 'FAIL: a second tag with the same name key in one ledger';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    -- The key is per ledger: the same key in another ledger is its own tag.
    INSERT INTO tags (ledger_id, name) VALUES (v_other, ' travel');

    -- ---- Snapshot capture ---------------------------------------------------
    IF NOT 'txn_leg_note_tags' = ANY (fn_ledger_snapshot_part_names()) THEN
        RAISE EXCEPTION 'FAIL: snapshots do not capture txn_leg_note_tags';
    END IF;
    IF array_position(fn_ledger_snapshot_insert_order(), 'txn_leg_note_tags')
         < array_position(fn_ledger_snapshot_insert_order(), 'txn_legs')
       OR array_position(fn_ledger_snapshot_insert_order(), 'txn_leg_note_tags')
         < array_position(fn_ledger_snapshot_insert_order(), 'tags')
       OR array_position(fn_ledger_snapshot_insert_order(), 'txn_leg_note_tags') IS NULL THEN
        RAISE EXCEPTION 'FAIL: a restore inserts txn_leg_note_tags before the legs or tags it references';
    END IF;

    -- Chunked on (leg_id, tag_id), one row per chunk: every resume compares a
    -- composite key, and every row lands in exactly one chunk.
    INSERT INTO ledger_snapshots (id, ledger_id, created_by_user_id, kind, schema_version,
                                  content, content_size_uncompressed)
         VALUES (v_snap, v_ledger, v_owner, 'manual', 'test', ''::bytea, 0);
    PERFORM fn_snapshot_write_part(v_snap, v_ledger, 'txn_leg_note_tags', 1);
    SELECT count(*) INTO v_n FROM ledger_snapshot_parts
     WHERE snapshot_id = v_snap AND part_name = 'txn_leg_note_tags';
    IF v_n <> 3 THEN
        RAISE EXCEPTION 'FAIL: three note-tag rows at one per chunk wrote % chunk(s)', v_n;
    END IF;
    IF (SELECT count(DISTINCT (e ->> 'leg_id', e ->> 'tag_id'))
          FROM ledger_snapshot_parts p, jsonb_array_elements(p.content) e
         WHERE p.snapshot_id = v_snap AND p.part_name = 'txn_leg_note_tags') <> 3
       OR (SELECT count(*)
             FROM ledger_snapshot_parts p, jsonb_array_elements(p.content) e
            WHERE p.snapshot_id = v_snap AND p.part_name = 'txn_leg_note_tags') <> 3 THEN
        RAISE EXCEPTION 'FAIL: the chunks lost or repeated a note-tag row';
    END IF;

    CREATE TEMP TABLE verify_no_primary_key (ledger_id uuid) ON COMMIT DROP;
    BEGIN
        PERFORM fn_snapshot_write_part(v_snap, v_ledger, 'verify_no_primary_key', 1);
        RAISE EXCEPTION 'FAIL: a table without a primary key was captured';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM LIKE 'FAIL:%' THEN RAISE; END IF;
    END;

    -- ---- Cascades -----------------------------------------------------------
    DELETE FROM tags WHERE id = v_tag_b;
    IF EXISTS (SELECT 1 FROM txn_leg_note_tags WHERE tag_id = v_tag_b) THEN
        RAISE EXCEPTION 'FAIL: a deleted tag left its note-tag rows';
    END IF;
    DELETE FROM txn_legs WHERE id = v_leg_cat;
    IF EXISTS (SELECT 1 FROM txn_leg_note_tags WHERE leg_id = v_leg_cat) THEN
        RAISE EXCEPTION 'FAIL: a deleted leg left its note-tag rows';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM txn_leg_note_tags WHERE leg_id = v_leg_bank AND tag_id = v_tag_a) THEN
        RAISE EXCEPTION 'FAIL: a cascade removed an unrelated note-tag row';
    END IF;

    -- ---- The ordered locks, under READ COMMITTED ---------------------------
    SELECT locked INTO v_n FROM fn_lock_txn_headers(ARRAY[v_header, v_o_header, gen_random_uuid()]);
    IF v_n <> 2 THEN RAISE EXCEPTION 'FAIL: fn_lock_txn_headers locked % header(s), not 2', v_n; END IF;
    SELECT locked INTO v_n FROM fn_lock_tags(ARRAY[v_tag_a, v_tag_c]);
    IF v_n <> 2 THEN RAISE EXCEPTION 'FAIL: fn_lock_tags locked % tag(s), not 2', v_n; END IF;
    SELECT locked INTO v_n FROM fn_lock_accounts(ARRAY[v_bank]);
    IF v_n <> 1 THEN RAISE EXCEPTION 'FAIL: fn_lock_accounts locked % account(s), not 1', v_n; END IF;

    RAISE NOTICE 'OK: the stored presentation, its tags, its index, locks, view and capture';
END $$;

ROLLBACK;
