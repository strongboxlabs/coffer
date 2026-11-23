-- Verify the permanent objects of migration 263: tag_name_key / tag_name_keys,
-- saved_report_spec_tag_id, saved_reports_repoint_tag,
-- transfer_shares_source_brokerages, and the originating-account reference.
--
-- 263's backfill runs once over existing data, and its end assertion fails the
-- upgrade on a broken result; the C# migration tests drive it over fixtures. What
-- stays behind is checked here, because every writer and every later migration
-- relies on it:
--
--   * tag_name_key is the one definition of "the same tag name": lower() of the
--     name with leading and trailing .NET white space removed, and nothing else.
--   * saved_report_spec_tag_id never casts a malformed tagId; it answers NULL.
--   * saved_reports_repoint_tag runs as its owner, past RLS, so its own grant
--     check is the only access control it has. Both refusals are exercised, and
--     the one UPDATE it makes is pinned to one ledger, one tag and one field.
--   * transfer_shares_source_brokerages names exactly one source brokerage, by
--     posting where a posting pairs it, else by the holdings link.
--   * originating_account_id references an account of the header's own ledger,
--     and refuses that account's delete.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_originating_account_and_tag_name_key.sql
-- All assertions are plpgsql DO blocks; any failure aborts. ROLLBACK at the end.

BEGIN;

-- ---------------------------------------------------------------------------
-- tag_name_key / tag_name_keys
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_got text;
BEGIN
    IF public.tag_name_key('  Groceries ') IS DISTINCT FROM 'groceries' THEN
        RAISE EXCEPTION 'tag_name_key did not trim spaces and lower: got %', public.tag_name_key('  Groceries ');
    END IF;

    -- Every character of .NET's char.IsWhiteSpace set at either end goes: the
    -- C0 controls 0009-000D, 0085, 00A0, 1680, 2000-200A, 2028, 2029, 202F,
    -- 205F and 3000.
    v_got := public.tag_name_key(
        U&'\0009\000A\000B\000C\000D\0085\00A0\1680\2000\2005\200A\2028\2029\202F\205F\3000'
        || 'Food'
        || U&'\3000\205F\202F\2029\2028\200A\2005\2000\1680\00A0\0085\000D\000C\000B\000A\0009');
    IF v_got IS DISTINCT FROM 'food' THEN
        RAISE EXCEPTION 'tag_name_key left .NET white space at an end: got %', v_got;
    END IF;

    -- White space inside the name is part of the name.
    IF public.tag_name_key(U&' Home\00A0Office ') IS DISTINCT FROM U&'home\00A0office' THEN
        RAISE EXCEPTION 'tag_name_key changed white space inside a name';
    END IF;

    -- A character outside the set is kept, even where it looks blank: the zero
    -- width space (200B) is not white space to char.IsWhiteSpace.
    IF public.tag_name_key(U&'\200Bx') IS DISTINCT FROM U&'\200Bx' THEN
        RAISE EXCEPTION 'tag_name_key removed a character outside the white space set';
    END IF;

    IF public.tag_name_key(NULL) IS NOT NULL THEN
        RAISE EXCEPTION 'tag_name_key(NULL) is not NULL';
    END IF;

    -- The list form keys each name with the same function, one row per name.
    IF (SELECT count(*) FROM public.tag_name_keys(ARRAY['A ', ' b', 'A '])) <> 3
       OR EXISTS (SELECT 1 FROM public.tag_name_keys(ARRAY['A ', ' b', 'A ']) k
                   WHERE k.key IS DISTINCT FROM public.tag_name_key(k.name)) THEN
        RAISE EXCEPTION 'tag_name_keys disagrees with tag_name_key';
    END IF;

    RAISE NOTICE 'OK: tag_name_key / tag_name_keys';
END $$;

-- ---------------------------------------------------------------------------
-- saved_report_spec_tag_id
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_tag uuid := '6f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b';
BEGIN
    IF public.saved_report_spec_tag_id(jsonb_build_object('tagId', v_tag::text)) IS DISTINCT FROM v_tag THEN
        RAISE EXCEPTION 'saved_report_spec_tag_id did not read a hyphenated uuid string';
    END IF;
    IF public.saved_report_spec_tag_id(jsonb_build_object('tagId', upper(v_tag::text))) IS DISTINCT FROM v_tag THEN
        RAISE EXCEPTION 'saved_report_spec_tag_id did not read an upper-case uuid string';
    END IF;

    -- Anything else is no tag, and never an error.
    IF public.saved_report_spec_tag_id('{"tagId": 5}'::jsonb) IS NOT NULL
       OR public.saved_report_spec_tag_id('{"tagId": null}'::jsonb) IS NOT NULL
       OR public.saved_report_spec_tag_id('{"tagId": "not-a-uuid"}'::jsonb) IS NOT NULL
       OR public.saved_report_spec_tag_id(jsonb_build_object('tagId', replace(v_tag::text, '-', ''))) IS NOT NULL
       OR public.saved_report_spec_tag_id(jsonb_build_object('tagId', jsonb_build_array(v_tag::text))) IS NOT NULL
       OR public.saved_report_spec_tag_id('{}'::jsonb) IS NOT NULL THEN
        RAISE EXCEPTION 'saved_report_spec_tag_id read a tag from a spec that names none';
    END IF;

    RAISE NOTICE 'OK: saved_report_spec_tag_id';
END $$;

-- ---------------------------------------------------------------------------
-- saved_reports_repoint_tag
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_ledger      uuid := gen_random_uuid();
    v_other       uuid := gen_random_uuid();
    v_editor      uuid := gen_random_uuid();
    v_viewer      uuid := gen_random_uuid();
    v_member      uuid := gen_random_uuid();
    v_from        uuid := gen_random_uuid();
    v_to          uuid := gen_random_uuid();
    v_unrelated   uuid := gen_random_uuid();
    v_foreign_tag uuid := gen_random_uuid();
    v_stamp       timestamptz := TIMESTAMPTZ '2026-01-02 03:04:05+00';
    v_repointed   integer;
    v_visible     integer;
BEGIN
    INSERT INTO ledgers (id, name) VALUES (v_ledger, 'Repoint Test'), (v_other, 'Repoint Other');
    INSERT INTO users (id, display_name)
         VALUES (v_editor, 'editor'), (v_viewer, 'viewer'), (v_member, 'member');
    INSERT INTO user_ledger_grants (user_id, ledger_id, role)
         VALUES (v_member, v_ledger, 'owner'), (v_editor, v_ledger, 'editor'),
                (v_viewer, v_ledger, 'viewer'), (v_member, v_other, 'owner');
    INSERT INTO tags (id, ledger_id, name)
         VALUES (v_from, v_ledger, 'from'), (v_to, v_ledger, 'to'),
                (v_unrelated, v_ledger, 'unrelated'), (v_foreign_tag, v_other, 'foreign');

    INSERT INTO saved_reports (ledger_id, owner_user_id, name, spec, visibility, updated_at) VALUES
        (v_ledger, v_editor, 'editor own',      jsonb_build_object('tagId', v_from::text, 'kind', 'spending'), 'private', v_stamp),
        (v_ledger, v_member, 'member private',  jsonb_build_object('tagId', v_from::text), 'private', v_stamp),
        (v_ledger, v_member, 'member shared',   jsonb_build_object('tagId', v_from::text), 'ledger',  v_stamp),
        (v_ledger, v_member, 'other tag',       jsonb_build_object('tagId', v_unrelated::text), 'ledger', v_stamp),
        -- Another ledger's report naming this ledger's tag: never this ledger's.
        (v_other,  v_member, 'cross ledger',    jsonb_build_object('tagId', v_from::text), 'ledger',  v_stamp);

    -- No caller: refused.
    PERFORM set_config('app.user_id', '', true);
    BEGIN
        PERFORM public.saved_reports_repoint_tag(v_ledger, v_from, v_to);
        RAISE EXCEPTION 'FAIL: saved_reports_repoint_tag ran with no caller';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;

    -- A viewer: refused, as every role-gated write refuses one.
    PERFORM set_config('app.user_id', v_viewer::text, true);
    BEGIN
        PERFORM public.saved_reports_repoint_tag(v_ledger, v_from, v_to);
        RAISE EXCEPTION 'FAIL: saved_reports_repoint_tag ran for a viewer';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;

    PERFORM set_config('app.user_id', v_editor::text, true);

    -- The same tag twice, or a tag of another ledger: refused.
    BEGIN
        PERFORM public.saved_reports_repoint_tag(v_ledger, v_from, v_from);
        RAISE EXCEPTION 'FAIL: saved_reports_repoint_tag repointed a tag onto itself';
    EXCEPTION WHEN invalid_parameter_value THEN NULL;
    END;
    BEGIN
        PERFORM public.saved_reports_repoint_tag(v_ledger, v_from, v_foreign_tag);
        RAISE EXCEPTION 'FAIL: saved_reports_repoint_tag repointed onto another ledger''s tag';
    EXCEPTION WHEN invalid_parameter_value THEN NULL;
    END;

    IF EXISTS (SELECT 1 FROM saved_reports
                WHERE public.saved_report_spec_tag_id(spec) = v_to) THEN
        RAISE EXCEPTION 'FAIL: a refused repoint changed a report';
    END IF;

    SELECT r.repointed, r.repointed_visible INTO v_repointed, v_visible
      FROM public.saved_reports_repoint_tag(v_ledger, v_from, v_to) r;

    -- Three reports of this ledger named the tag; the editor sees its own and
    -- the shared one, never the other member's private one.
    IF v_repointed <> 3 OR v_visible <> 2 THEN
        RAISE EXCEPTION 'FAIL: repoint reported % changed, % visible (expected 3, 2)', v_repointed, v_visible;
    END IF;

    IF EXISTS (SELECT 1 FROM saved_reports
                WHERE ledger_id = v_ledger AND public.saved_report_spec_tag_id(spec) = v_from) THEN
        RAISE EXCEPTION 'FAIL: a report of the ledger still names the merged tag';
    END IF;
    IF (SELECT public.saved_report_spec_tag_id(spec) FROM saved_reports WHERE name = 'cross ledger') IS DISTINCT FROM v_from THEN
        RAISE EXCEPTION 'FAIL: the repoint reached a report of another ledger';
    END IF;
    IF (SELECT public.saved_report_spec_tag_id(spec) FROM saved_reports WHERE name = 'other tag') IS DISTINCT FROM v_unrelated THEN
        RAISE EXCEPTION 'FAIL: the repoint changed a report on another tag';
    END IF;

    -- spec.tagId is the one field it writes: the rest of the spec and the
    -- report's updated_at stay, since its owner did not edit it.
    IF (SELECT spec ->> 'kind' FROM saved_reports WHERE name = 'editor own') IS DISTINCT FROM 'spending' THEN
        RAISE EXCEPTION 'FAIL: the repoint changed another field of the spec';
    END IF;
    IF EXISTS (SELECT 1 FROM saved_reports WHERE ledger_id IN (v_ledger, v_other) AND updated_at <> v_stamp) THEN
        RAISE EXCEPTION 'FAIL: the repoint moved a report''s updated_at';
    END IF;

    RAISE NOTICE 'OK: saved_reports_repoint_tag';
END $$;

-- ---------------------------------------------------------------------------
-- transfer_shares_source_brokerages, and the originating-account reference
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_ledger    uuid := gen_random_uuid();
    v_other     uuid := gen_random_uuid();
    v_src       uuid := gen_random_uuid();
    v_src_h     uuid := gen_random_uuid();
    v_dst       uuid := gen_random_uuid();
    v_dst_h     uuid := gen_random_uuid();
    v_twin      uuid := gen_random_uuid();
    v_cat       uuid := gen_random_uuid();
    v_unused    uuid := gen_random_uuid();
    v_foreign   uuid := gen_random_uuid();
    v_strict    uuid := gen_random_uuid();
    v_fallback  uuid := gen_random_uuid();
    v_sell       uuid := gen_random_uuid();
    v_legless   uuid := gen_random_uuid();
    v_sec       uuid := gen_random_uuid();
    v_day       timestamptz := TIMESTAMPTZ '2026-02-03 12:00:00+00';
    v_rows      integer;
BEGIN
    INSERT INTO ledgers (id, name) VALUES (v_ledger, 'Source Test'), (v_other, 'Source Other');
    INSERT INTO accounts (id, ledger_id, name, account_type) VALUES
        (v_src_h, v_ledger, 'source holdings', 'investment'),
        (v_dst_h, v_ledger, 'dest holdings',   'investment');
    -- Two brokerages name the source holdings account: holdings_account_id is
    -- not unique, and only the posting tells them apart.
    INSERT INTO accounts (id, ledger_id, name, account_type, holdings_account_id) VALUES
        (v_src,  v_ledger, 'source brokerage', 'investment', v_src_h),
        (v_twin, v_ledger, 'twin brokerage',   'investment', v_src_h),
        (v_dst,  v_ledger, 'dest brokerage',   'investment', v_dst_h);
    INSERT INTO accounts (id, ledger_id, name, account_type, category_kind)
         VALUES (v_cat, v_ledger, 'category', 'category', 'expense');
    INSERT INTO accounts (id, ledger_id, name, account_type)
         VALUES (v_unused, v_ledger, 'no legs', 'bank');
    INSERT INTO accounts (id, ledger_id, name, account_type)
         VALUES (v_foreign, v_other, 'foreign', 'bank');
    INSERT INTO securities (id, ledger_id, ticker, name) VALUES (v_sec, v_ledger, 'SECA', 'security');

    -- Strict form: the shares leave the source holdings in the posting that
    -- pairs them with the source brokerage.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee, posted_at, transacted_at, created_at,
                             originating_account_id)
         VALUES (v_strict, v_ledger, 'manual', 'transfer_shares', 'in-kind', v_day, v_day, v_day, v_src);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, security_id, quantity) VALUES
        (gen_random_uuid(), v_strict, v_ledger, v_src_h, 0, -500, v_sec, -5),
        (gen_random_uuid(), v_strict, v_ledger, v_src,   0,  500, NULL,  NULL),
        (gen_random_uuid(), v_strict, v_ledger, v_dst_h, 1,  500, v_sec,  5),
        (gen_random_uuid(), v_strict, v_ledger, v_dst,   1, -500, NULL,  NULL);

    -- Fallback form: no posting pairs the source holdings leg with a brokerage
    -- (it faces a category); the source brokerage has a leg elsewhere on the
    -- header.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee, posted_at, transacted_at, created_at,
                             originating_account_id)
         VALUES (v_fallback, v_ledger, 'manual', 'transfer_shares', 'in-kind', v_day, v_day, v_day, v_src);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, security_id, quantity) VALUES
        (gen_random_uuid(), v_fallback, v_ledger, v_src_h, 0, -300, v_sec, -3),
        (gen_random_uuid(), v_fallback, v_ledger, v_cat,   0,  300, NULL,  NULL),
        (gen_random_uuid(), v_fallback, v_ledger, v_src,   1,    0, NULL,  NULL),
        (gen_random_uuid(), v_fallback, v_ledger, v_dst,   1,    0, NULL,  NULL);

    -- Not an in-kind transfer: never listed, whatever its shape.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee, posted_at, transacted_at, created_at,
                             originating_account_id)
         VALUES (v_sell, v_ledger, 'manual', 'sell', 'sell', v_day, v_day, v_day, v_src);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, security_id, quantity) VALUES
        (gen_random_uuid(), v_sell, v_ledger, v_src_h, 0, -200, v_sec, -2),
        (gen_random_uuid(), v_sell, v_ledger, v_src,   0,  200, NULL,  NULL);

    IF (SELECT count(*) FROM public.transfer_shares_source_brokerages(v_ledger) s WHERE s.header_id = v_strict) <> 1
       OR NOT EXISTS (SELECT 1 FROM public.transfer_shares_source_brokerages(v_ledger) s
                       WHERE s.header_id = v_strict AND s.account_id = v_src) THEN
        RAISE EXCEPTION 'FAIL: the strict form did not name the source brokerage alone';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.transfer_shares_source_brokerages(v_ledger) s
                    WHERE s.header_id = v_fallback AND s.account_id = v_src)
       OR EXISTS (SELECT 1 FROM public.transfer_shares_source_brokerages(v_ledger) s
                   WHERE s.header_id = v_fallback AND s.account_id <> v_src) THEN
        RAISE EXCEPTION 'FAIL: the fallback form did not name the source brokerage alone';
    END IF;
    IF EXISTS (SELECT 1 FROM public.transfer_shares_source_brokerages(v_ledger) s WHERE s.header_id = v_sell) THEN
        RAISE EXCEPTION 'FAIL: a header that is not an in-kind transfer was listed';
    END IF;
    SELECT count(*) INTO v_rows FROM public.transfer_shares_source_brokerages(v_other);
    IF v_rows <> 0 THEN
        RAISE EXCEPTION 'FAIL: another ledger''s call listed % header(s)', v_rows;
    END IF;

    -- The reference is composite: an account of another ledger is refused.
    BEGIN
        UPDATE txn_headers SET originating_account_id = v_foreign WHERE id = v_sell;
        RAISE EXCEPTION 'FAIL: a header took an originating account of another ledger';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- RESTRICT: the originating account cannot be deleted from under its header.
    -- A header with no legs is the only way to see this constraint act alone
    -- (with legs, their own reference refuses first); no writer leaves a legless
    -- header naming an account, so this row exists only in this script.
    INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, created_at,
                             originating_account_id)
         VALUES (v_legless, v_ledger, 'manual', 'legless', v_day, v_day, v_day, v_unused);
    BEGIN
        DELETE FROM accounts WHERE id = v_unused;
        RAISE EXCEPTION 'FAIL: a header''s originating account was deleted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    RAISE NOTICE 'OK: transfer_shares_source_brokerages and the originating-account reference';
END $$;

ROLLBACK;
