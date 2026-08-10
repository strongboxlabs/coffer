-- Sanity-check register_entry_keys after ADR-0034 v2. The function
-- drives the API's entry-keyed register pagination — a multi-leg
-- event returns as ONE entry, paged independently of leg count. With
-- ADR-0034 v2 the entry_key is always h.id (no l.id fallback for
-- single-posting events); mig 166 made the cursor entry-key based —
-- the pair is now (entry_key, seq). Mig 167 (ADR-0076) factored the filter
-- WHERE into register_filtered_entries (the single source of truth the page,
-- rail, and counts share); Test 4 smoke-checks that primitive.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_register_entry_functions.sql
-- All assertions use plpgsql DO blocks; any failure aborts the script.

BEGIN;

-- ---------------------------------------------------------------------------
-- Fixture: a fresh test ledger (so the ledger-wide assertion in Test 3
-- doesn't collide with real data on populated DBs), one bank account,
-- three category accounts, and:
--   * five single-posting pair events across five distinct dates
--   * one 3-posting (multi-split) event on a sixth date
-- The ROLLBACK at the end of the script discards everything.
-- ---------------------------------------------------------------------------

INSERT INTO ledgers (id, name)
VALUES ('aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', 'REGFN Test Ledger');

INSERT INTO accounts (id, ledger_id, name, account_type, opening_balance)
VALUES ('aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
        'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
        'REGFN Checking', 'bank', 0);

INSERT INTO accounts (id, ledger_id, name, account_type, category_kind) VALUES
    ('aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa',
     'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
     'REGFN Groceries', 'category', 'expense'),
    ('aaaaaaaa-3333-3333-3333-aaaaaaaaaaaa',
     'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
     'REGFN Federal Tax', 'category', 'expense'),
    ('aaaaaaaa-4444-4444-4444-aaaaaaaaaaaa',
     'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
     'REGFN State Tax', 'category', 'expense');

-- Helper: insert a single-posting event (1 header, 1 posting, 2 legs).
-- Under ADR-0034 v2 entry_key is h.id regardless of multi/single-
-- posting shape, so both legs of this event group into one entry.
CREATE OR REPLACE FUNCTION regfn_insert_pair(
    p_cash_id     UUID,
    p_cat_id      UUID,
    p_amount      NUMERIC,
    p_posted_at   TIMESTAMPTZ,
    p_external    TEXT
) RETURNS VOID AS $$
DECLARE
    v_header UUID := gen_random_uuid();
BEGIN
    -- Mig 107: origin is icon-level; provider_key is the
    -- per-provider tag. Manual-shape rows leave provider_key NULL.
    INSERT INTO txn_headers (id, ledger_id, origin, external_id, payee, posted_at, transacted_at)
    VALUES (v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
            'manual', p_external, 'single-test', p_posted_at, p_posted_at);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
    VALUES
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cash_id, 0,  p_amount),
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cat_id,  0, -p_amount);
    -- Mig 120: recompute the denormalized posting counts (a single
    -- posting → 1, which matches the column DEFAULT, but recompute
    -- anyway so the fixture never silently relies on the default).
    PERFORM fn_recompute_posting_counts_for_header(v_header);
END;
$$ LANGUAGE plpgsql;

-- Helper: insert a 3-posting multi-split event (1 header, 3 postings,
-- 6 legs). All 6 legs group by h.id into one entry.
-- Returns the header id (the entry identity under ADR-0034 v2).
CREATE OR REPLACE FUNCTION regfn_insert_split3(
    p_cash_id      UUID,
    p_cat1_id      UUID, p_amt1 NUMERIC,
    p_cat2_id      UUID, p_amt2 NUMERIC,
    p_cat3_id      UUID, p_amt3 NUMERIC,
    p_posted_at    TIMESTAMPTZ,
    p_external     TEXT
) RETURNS UUID AS $$
DECLARE
    v_header UUID := gen_random_uuid();
BEGIN
    INSERT INTO txn_headers (id, ledger_id, origin, external_id, payee, posted_at, transacted_at)
    VALUES (v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
            'manual', p_external, 'split-test', p_posted_at, p_posted_at);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
    VALUES
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cash_id, 0,  p_amt1),
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cat1_id, 0, -p_amt1),
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cash_id, 1,  p_amt2),
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cat2_id, 1, -p_amt2),
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cash_id, 2,  p_amt3),
        (gen_random_uuid(), v_header, 'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', p_cat3_id, 2, -p_amt3);
    -- Mig 120: these raw inserts bypass the recompute interceptor, so the
    -- denormalized posting counts would sit at DEFAULT 1 and the
    -- originating/target split (ADR-0036) would misfire. Recompute the
    -- header's legs exactly as the API + importer do post-write.
    PERFORM fn_recompute_posting_counts_for_header(v_header);
    RETURN v_header;
END;
$$ LANGUAGE plpgsql;

-- Seed: 5 singles on 2026-05-01..05, then a 3-posting split on 2026-05-15.
SELECT regfn_insert_pair(
    'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
    'aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa',
    -10, '2026-05-01T12:00:00Z', 'regfn-single-1');
SELECT regfn_insert_pair(
    'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
    'aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa',
    -20, '2026-05-02T12:00:00Z', 'regfn-single-2');
SELECT regfn_insert_pair(
    'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
    'aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa',
    -30, '2026-05-03T12:00:00Z', 'regfn-single-3');
SELECT regfn_insert_pair(
    'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
    'aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa',
    -40, '2026-05-04T12:00:00Z', 'regfn-single-4');
SELECT regfn_insert_pair(
    'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
    'aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa',
    -50, '2026-05-05T12:00:00Z', 'regfn-single-5');

DO $$
DECLARE v_group_id UUID;
BEGIN
    v_group_id := regfn_insert_split3(
        'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
        'aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa', -100,
        'aaaaaaaa-3333-3333-3333-aaaaaaaaaaaa', -50,
        'aaaaaaaa-4444-4444-4444-aaaaaaaaaaaa', -350,
        '2026-05-15T12:00:00Z', 'regfn-split-1');
    -- Stash the header id so subsequent DO blocks can read it back.
    CREATE TEMP TABLE regfn_state AS SELECT v_group_id AS group_id;
END $$;

-- ---------------------------------------------------------------------------
-- Test 1: register_entry_keys on the bank account returns 6 entries
-- (5 singles + 1 split), DESC by (posted_at, seq), with the split's
-- entry_key equal to the seeded header id.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_count INT;
    v_top_key UUID;
    v_expected_group UUID;
BEGIN
    SELECT group_id INTO v_expected_group FROM regfn_state;

    -- Mig 166 signature: (account, ledger, cursor_entry_key, cursor_seq,
    -- direction, limit, …) — the trailing sort/filter params default, so
    -- 6 positional args exercise the plain register.
    SELECT COUNT(*) INTO v_count
    FROM register_entry_keys(
        'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
        'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
        NULL::UUID, NULL::BIGINT, 'before', 100);
    IF v_count <> 6 THEN
        RAISE EXCEPTION
            'register_entry_keys: expected 6 entries on bank account, got %',
            v_count;
    END IF;

    -- First entry (DESC by posted_at) is the 2026-05-15 group.
    SELECT entry_key INTO v_top_key
    FROM register_entry_keys(
        'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
        'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
        NULL::UUID, NULL::BIGINT, 'before', 1);
    IF v_top_key <> v_expected_group THEN
        RAISE EXCEPTION
            'register_entry_keys: first entry_key should be the header id %, got %',
            v_expected_group, v_top_key;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- Test 2: cursor-based pagination walks through ALL 6 entries with
-- limit=2 across 3 pages, visiting each entry exactly once, never
-- splitting the group. Cursor is now (entry_key, seq) (mig 166).
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_total INT := 0;
    v_groups_seen INT := 0;
    v_cursor_entry_key UUID;
    v_cursor_seq BIGINT;
    v_loop_count INT := 0;
    v_page_count INT;
    v_group_id UUID;
    r RECORD;
BEGIN
    SELECT group_id INTO v_group_id FROM regfn_state;
    v_cursor_entry_key := NULL;
    v_cursor_seq := NULL;
    LOOP
        v_loop_count := v_loop_count + 1;
        IF v_loop_count > 10 THEN
            RAISE EXCEPTION 'register_entry_keys: pagination loop runaway';
        END IF;

        v_page_count := 0;
        FOR r IN
            SELECT posted_at, seq, entry_key
            FROM register_entry_keys(
                'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
                'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
                v_cursor_entry_key, v_cursor_seq, 'before', 2)
        LOOP
            v_total := v_total + 1;
            v_page_count := v_page_count + 1;
            IF r.entry_key = v_group_id THEN
                v_groups_seen := v_groups_seen + 1;
            END IF;
            v_cursor_entry_key := r.entry_key;
            v_cursor_seq := r.seq;
        END LOOP;
        EXIT WHEN v_page_count < 2;
    END LOOP;

    IF v_total <> 6 THEN
        RAISE EXCEPTION
            'pagination total: expected 6 entries, walked %', v_total;
    END IF;
    IF v_groups_seen <> 1 THEN
        RAISE EXCEPTION
            'pagination groups: expected 1 group entry, got %', v_groups_seen;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- Test 3: NULL p_account_id (ledger-wide) scopes to the right ledger
-- via the EXISTS-on-accounts predicate. ADR-0036: entry_key
-- derivation is asymmetric — originating-side rows (account is
-- touched by every posting of the header) bucket under header_id;
-- target-side rows (touched by some but not all postings) bucket
-- per leg id. A ledger-wide scan sees the same header from BOTH
-- sides simultaneously, so the originating + target entries co-exist.
--
-- Expected count from the fixture:
--   5 singles  → 5 entries (both legs of each — Checking +
--                Groceries — have account_postings_on_header=1 and
--                header_total_postings=1, so each leg's entry_key
--                collapses to header_id; both legs share one entry)
--   1 split    → 4 entries:
--                  * 1 ORIGINATING from Checking (3/3 postings →
--                    entry_key = header_id, all 3 cash legs collapse)
--                  * 3 TARGET, one per category leg (Groceries,
--                    Federal Tax, State Tax) — each is 1/3 postings
--                    on its account, so entry_key = leg id
--   = 9 entries ledger-wide.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_count INT;
BEGIN
    SELECT COUNT(*) INTO v_count
    FROM register_entry_keys(
        NULL,
        'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa',
        NULL::UUID, NULL::BIGINT, 'before', 100);
    IF v_count <> 9 THEN
        RAISE EXCEPTION
            'register_entry_keys: ledger-wide view should see exactly 9 entries (5 singles + 1 originating + 3 targets), got %',
            v_count;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- Test 4 (mig 167 / ADR-0076): register_filtered_entries is the shared filter
-- primitive. Smoke it directly — callable, and each dimension selects the
-- expected header set (per-leg filter, DISTINCT to header = "any leg matches").
--   * unfiltered on checking → 6 headers (5 singles + 1 split), matching
--     register_entry_keys' 6 entries in Test 1.
--   * search 'split-test' → only the multi-split header.
--   * amount_min 35 → single-4 (40) + single-5 (50) + the split = 3 headers.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    v_all INT;
    v_search INT;
    v_amount INT;
BEGIN
    SELECT COUNT(DISTINCT header_id) INTO v_all
    FROM register_filtered_entries(
        'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
        'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', FALSE);
    IF v_all <> 6 THEN
        RAISE EXCEPTION
            'register_filtered_entries: unfiltered should see 6 headers on checking, got %', v_all;
    END IF;

    SELECT COUNT(DISTINCT header_id) INTO v_search
    FROM register_filtered_entries(
        'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
        'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', FALSE, 'split-test');
    IF v_search <> 1 THEN
        RAISE EXCEPTION
            'register_filtered_entries: search=split-test should match 1 header, got %', v_search;
    END IF;

    SELECT COUNT(DISTINCT header_id) INTO v_amount
    FROM register_filtered_entries(
        'aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa',
        'aaaaaaaa-0000-0000-0000-aaaaaaaaaaaa', FALSE, NULL, NULL, NULL, 35);
    IF v_amount <> 3 THEN
        RAISE EXCEPTION
            'register_filtered_entries: amount_min=35 should match 3 headers, got %', v_amount;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- Clean up the fixture.
-- ---------------------------------------------------------------------------
DROP FUNCTION regfn_insert_split3(UUID, UUID, NUMERIC, UUID, NUMERIC, UUID, NUMERIC, TIMESTAMPTZ, TEXT);
DROP FUNCTION regfn_insert_pair(UUID, UUID, NUMERIC, TIMESTAMPTZ, TEXT);
DROP TABLE regfn_state;

-- Note: Q2 ("rows for these entries") used to be a SETOF Postgres
-- function. We dropped it after measurement showed it added 40× of
-- latency vs an equivalent direct view query (Postgres declined to
-- inline the SETOF function under our calling pattern). The API now
-- expresses Q2 as LINQ over `resolved_transactions`. Q2's behaviour
-- is covered by the C# integration tests
-- (Get_collapses_multi_split_legs_into_one_group_entry,
-- Get_paginates_by_entry_never_slicing_a_group, etc.) rather than
-- here.

ROLLBACK;
