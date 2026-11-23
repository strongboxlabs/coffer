-- Verify migration 266: resolved_transactions, live_txn_headers and
-- template_txn_headers carry no memo or tag column of their own, the register's
-- SQL functions are gone, and the recreated views keep the RLS boundary.
--
-- A memo or tag a SQL reader could reach through these views would be a second
-- definition of what a row shows, beside the stored presentation (264). And a
-- view that is dropped and created again loses security_invoker and its grant,
-- so it would read every ledger's rows as its owner, or none as the request
-- role. Both halves are checked: the columns and functions by the catalog, the
-- boundary by reading the views as the request role.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_views_without_notes_columns.sql
-- All assertions are plpgsql DO blocks; any failure aborts. ROLLBACK at the end.

BEGIN;

-- ---------------------------------------------------------------------------
-- The catalog: columns, functions, reloptions, grants.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_found text;
    v_view  text;
BEGIN
    SELECT string_agg(table_name || '.' || column_name, ', ' ORDER BY table_name, column_name)
      INTO v_found
      FROM information_schema.columns
     WHERE table_schema = 'public'
       AND ((table_name = 'resolved_transactions' AND column_name IN ('memo', 'tags', 'leg_memo', 'header_memo'))
         OR (table_name IN ('live_txn_headers', 'template_txn_headers') AND column_name = 'memo'));
    IF v_found IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: a view still carries a memo or tag column of its own: %', v_found;
    END IF;

    -- The presentation the readers read instead is there, for both legs.
    IF (SELECT count(*) FROM information_schema.columns
         WHERE table_schema = 'public' AND table_name = 'resolved_transactions'
           AND column_name IN ('notes_memo', 'counterparty_notes_memo')) <> 2 THEN
        RAISE EXCEPTION 'FAIL: resolved_transactions lost the stored presentation''s columns';
    END IF;

    SELECT string_agg(p.oid::regprocedure::text, ', ')
      INTO v_found
      FROM pg_proc p
     WHERE p.proname IN ('register_entry_keys', 'register_filtered_entries');
    IF v_found IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: a register SQL function survives: %', v_found;
    END IF;

    FOREACH v_view IN ARRAY ARRAY['resolved_transactions', 'live_txn_headers', 'template_txn_headers'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_class c
                        WHERE c.oid = format('public.%I', v_view)::regclass
                          AND 'security_invoker=true' = ANY (c.reloptions)) THEN
            RAISE EXCEPTION 'FAIL: % is not security_invoker', v_view;
        END IF;
        IF NOT has_table_privilege('coffer_app', format('public.%I', v_view), 'SELECT') THEN
            RAISE EXCEPTION 'FAIL: coffer_app cannot SELECT %', v_view;
        END IF;
    END LOOP;

    RAISE NOTICE 'OK: the views'' columns, the dropped functions, security_invoker and grants';
END $$;

-- ---------------------------------------------------------------------------
-- The boundary: what the request role reads through each view.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_ledger   uuid := gen_random_uuid();
    v_member   uuid := gen_random_uuid();
    v_stranger uuid := gen_random_uuid();
    v_bank     uuid := gen_random_uuid();
    v_cat      uuid := gen_random_uuid();
    v_live     uuid := gen_random_uuid();
    v_template uuid := gen_random_uuid();
    v_day      timestamptz := TIMESTAMPTZ '2026-04-05 12:00:00+00';
    v_rows     integer;
    v_live_n   integer;
    v_tmpl_n   integer;
BEGIN
    INSERT INTO ledgers (id, name) VALUES (v_ledger, 'View Test');
    INSERT INTO users (id, display_name) VALUES (v_member, 'member'), (v_stranger, 'stranger');
    INSERT INTO user_ledger_grants (user_id, ledger_id, role) VALUES (v_member, v_ledger, 'viewer');
    INSERT INTO accounts (id, ledger_id, name, account_type) VALUES (v_bank, v_ledger, 'bank', 'bank');
    INSERT INTO accounts (id, ledger_id, name, account_type, category_kind)
         VALUES (v_cat, v_ledger, 'category', 'category', 'expense');
    INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, created_at,
                             originating_account_id, is_recurring_template) VALUES
        (v_live,     v_ledger, 'manual', 'live',     v_day, v_day, v_day, v_bank, false),
        (v_template, v_ledger, 'manual', 'template', v_day, v_day, v_day, v_bank, true);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount) VALUES
        (gen_random_uuid(), v_live,     v_ledger, v_bank, 0, -10),
        (gen_random_uuid(), v_live,     v_ledger, v_cat,  0,  10),
        (gen_random_uuid(), v_template, v_ledger, v_bank, 0, -10),
        (gen_random_uuid(), v_template, v_ledger, v_cat,  0,  10);

    -- A member reads the ledger's rows: both legs of the live header, the live
    -- header in the live view and the template only in the template view.
    PERFORM set_config('app.user_id', v_member::text, true);
    SET LOCAL ROLE coffer_app;
    SELECT count(*) INTO v_rows FROM resolved_transactions WHERE header_id IN (v_live, v_template);
    SELECT count(*) INTO v_live_n FROM live_txn_headers WHERE id IN (v_live, v_template);
    SELECT count(*) INTO v_tmpl_n FROM template_txn_headers WHERE id IN (v_live, v_template);
    RESET ROLE;
    IF v_rows <> 2 OR v_live_n <> 1 OR v_tmpl_n <> 1 THEN
        RAISE EXCEPTION 'FAIL: a member reads % view row(s), % live and % template header(s) (expected 2, 1, 1)',
            v_rows, v_live_n, v_tmpl_n;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM live_txn_headers WHERE id = v_live)
       OR NOT EXISTS (SELECT 1 FROM template_txn_headers WHERE id = v_template) THEN
        RAISE EXCEPTION 'FAIL: the live and template views split the headers the wrong way';
    END IF;

    -- A user with no grant on the ledger reads none of it through any view.
    PERFORM set_config('app.user_id', v_stranger::text, true);
    SET LOCAL ROLE coffer_app;
    SELECT count(*) INTO v_rows FROM resolved_transactions WHERE header_id IN (v_live, v_template);
    SELECT count(*) INTO v_live_n FROM live_txn_headers WHERE id IN (v_live, v_template);
    SELECT count(*) INTO v_tmpl_n FROM template_txn_headers WHERE id IN (v_live, v_template);
    RESET ROLE;
    PERFORM set_config('app.user_id', '', true);
    IF v_rows + v_live_n + v_tmpl_n <> 0 THEN
        RAISE EXCEPTION 'FAIL: a user with no grant reads % view row(s), % live and % template header(s)',
            v_rows, v_live_n, v_tmpl_n;
    END IF;

    RAISE NOTICE 'OK: the views read as the request role, within its grants';
END $$;

ROLLBACK;
