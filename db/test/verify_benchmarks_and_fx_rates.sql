-- Verify migration 280 (ADR-0114 D6, D7, D10, D12): the reporting-currency columns and
-- the five outside market-series tables refuse what their constraints say they refuse.
--
-- Case 1 is the currency columns: a ledger written without a code takes 'USD', and a
--   budget target written without one is refused.
-- Case 2 is ledger_benchmarks: a symbol once per ledger, positions 1 to 3, a position
--   once per ledger at commit but swappable inside a transaction, no blank symbol.
-- Case 3 is benchmark_growth: a positive growth, 'fetch' only, one value per day.
-- Case 4 is fx_rates: two different currencies, a positive rate, the two sources, a
--   user only on a manual rate, an upstream only on a fetched one, and one rate per day
--   whatever its source.
-- Case 5 is the attribution: deleting the author of a manual rate keeps the rate,
--   marked manual, with no author.
-- Case 6 is the coverage tables: fx_pair_coverage two different currencies, one row
--   per pair, a range that names its from day and does not run backwards;
--   benchmark_coverage one row per symbol, no blank symbol, no backwards range, kept
--   when its benchmark is removed.
-- Case 7 is the catalog: the request role cannot rewrite or remove a stored day, can
--   widen but not remove a coverage range, and the snapshot functions name all five
--   tables.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_benchmarks_and_fx_rates.sql
-- All assertions are plpgsql DO blocks; any failure aborts. The transaction ends in
-- ROLLBACK.

BEGIN;

DO $$
DECLARE
    v_ledger    uuid := gen_random_uuid();
    v_category  uuid := gen_random_uuid();
    v_author    uuid := gen_random_uuid();
    v_first     uuid := gen_random_uuid();
    v_second    uuid := gen_random_uuid();
    v_rate      uuid := gen_random_uuid();
    v_code      text;
    v_source    text;
    v_user      uuid;
BEGIN
    -- CASE 1 — the currency columns.
    INSERT INTO ledgers (id, name) VALUES (v_ledger, 'Market Series Test');
    SELECT currency_code INTO v_code FROM ledgers WHERE id = v_ledger;
    IF v_code IS DISTINCT FROM 'USD' THEN
        RAISE EXCEPTION 'FAIL: a ledger written without a code took %, expected USD', v_code;
    END IF;

    INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code,
                          opening_balance, is_active)
         VALUES (v_category, v_ledger, 'Category A', 'category', 'expense', 'USD', 0, TRUE);
    BEGIN
        INSERT INTO budget_targets (ledger_id, category_id, target_month, amount)
             VALUES (v_ledger, v_category, DATE '2026-01-01', 1.00);
        RAISE EXCEPTION 'FAIL: a budget target without a currency was stored';
    EXCEPTION WHEN not_null_violation THEN NULL;
    END;
    INSERT INTO budget_targets (ledger_id, category_id, target_month, amount, currency_code)
         VALUES (v_ledger, v_category, DATE '2026-01-01', 1.00, 'EUR');

    -- CASE 2 — ledger_benchmarks.
    INSERT INTO ledger_benchmarks (id, ledger_id, symbol, name, currency_code, position)
         VALUES (v_first,  v_ledger, 'FNDA', 'Fund A', 'USD', 1),
                (v_second, v_ledger, 'FNDB', 'Fund B', 'EUR', 2);
    BEGIN
        INSERT INTO ledger_benchmarks (ledger_id, symbol, name, currency_code, position)
             VALUES (v_ledger, 'FNDA', 'Fund A again', 'USD', 3);
        RAISE EXCEPTION 'FAIL: a symbol was stored twice in one ledger';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO ledger_benchmarks (ledger_id, symbol, name, currency_code, position)
             VALUES (v_ledger, 'FNDC', 'Fund C', 'USD', 4);
        RAISE EXCEPTION 'FAIL: a fourth position was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO ledger_benchmarks (ledger_id, symbol, name, currency_code, position)
             VALUES (v_ledger, '  ', 'Blank', 'USD', 3);
        RAISE EXCEPTION 'FAIL: a blank symbol was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    -- A swap passes: the position key is checked at the end, not per statement.
    UPDATE ledger_benchmarks SET position = 2 WHERE id = v_first;
    UPDATE ledger_benchmarks SET position = 1 WHERE id = v_second;
    SET CONSTRAINTS uq_ledger_benchmarks_position IMMEDIATE;
    SET CONSTRAINTS uq_ledger_benchmarks_position DEFERRED;
    -- Two rows left on one position fail when the check fires.
    BEGIN
        UPDATE ledger_benchmarks SET position = 1 WHERE id = v_first;
        SET CONSTRAINTS uq_ledger_benchmarks_position IMMEDIATE;
        RAISE EXCEPTION 'FAIL: two benchmarks of one ledger held one position';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    SET CONSTRAINTS uq_ledger_benchmarks_position DEFERRED;

    -- CASE 3 — benchmark_growth.
    INSERT INTO benchmark_growth (ledger_id, symbol, growth_date, growth, source)
         VALUES (v_ledger, 'FNDA', DATE '2026-01-05', 1.000123456789, 'fetch');
    BEGIN
        INSERT INTO benchmark_growth (ledger_id, symbol, growth_date, growth, source)
             VALUES (v_ledger, 'FNDA', DATE '2026-01-06', 0, 'fetch');
        RAISE EXCEPTION 'FAIL: a zero growth was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO benchmark_growth (ledger_id, symbol, growth_date, growth, source)
             VALUES (v_ledger, 'FNDA', DATE '2026-01-06', 1.01, 'manual');
        RAISE EXCEPTION 'FAIL: a hand-entered growth was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO benchmark_growth (ledger_id, symbol, growth_date, growth, source)
             VALUES (v_ledger, 'FNDA', DATE '2026-01-05', 1.02, 'fetch');
        RAISE EXCEPTION 'FAIL: a day of growth was stored twice';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    -- CASE 4 — fx_rates.
    INSERT INTO users (id, display_name, username, created_by)
         VALUES (v_author, 'Rate author', 'rate-author-' || v_author::text, 'verify-script');
    INSERT INTO fx_rates (ledger_id, from_currency, to_currency, rate_date, rate, source, upstream)
         VALUES (v_ledger, 'EUR', 'USD', DATE '2026-01-05', 1.234567890123, 'frankfurter', 'Source A');
    BEGIN
        INSERT INTO fx_rates (ledger_id, from_currency, to_currency, rate_date, rate, source)
             VALUES (v_ledger, 'USD', 'USD', DATE '2026-01-05', 1, 'frankfurter');
        RAISE EXCEPTION 'FAIL: a rate from a currency to itself was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_rates (ledger_id, from_currency, to_currency, rate_date, rate, source)
             VALUES (v_ledger, 'GBP', 'USD', DATE '2026-01-05', -1, 'frankfurter');
        RAISE EXCEPTION 'FAIL: a negative rate was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_rates (ledger_id, from_currency, to_currency, rate_date, rate, source)
             VALUES (v_ledger, 'GBP', 'USD', DATE '2026-01-05', 1.3, 'guess');
        RAISE EXCEPTION 'FAIL: a rate from an unknown source was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_rates (ledger_id, from_currency, to_currency, rate_date, rate, source, entered_by_user_id)
             VALUES (v_ledger, 'GBP', 'USD', DATE '2026-01-05', 1.3, 'frankfurter', v_author);
        RAISE EXCEPTION 'FAIL: a fetched rate named a user';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_rates (ledger_id, from_currency, to_currency, rate_date, rate, source, upstream, entered_by_user_id)
             VALUES (v_ledger, 'GBP', 'USD', DATE '2026-01-05', 1.3, 'manual', 'Source A', v_author);
        RAISE EXCEPTION 'FAIL: a manual rate named an upstream source';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_rates (ledger_id, from_currency, to_currency, rate_date, rate, source, entered_by_user_id)
             VALUES (v_ledger, 'EUR', 'USD', DATE '2026-01-05', 1.3, 'manual', v_author);
        RAISE EXCEPTION 'FAIL: a manual rate was stored for a day that holds a fetched one';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    INSERT INTO fx_rates (id, ledger_id, from_currency, to_currency, rate_date, rate, source, entered_by_user_id)
         VALUES (v_rate, v_ledger, 'EUR', 'USD', DATE '2026-01-06', 1.3, 'manual', v_author);

    -- CASE 5 — the author goes, the rate stays, still marked manual.
    DELETE FROM users WHERE id = v_author;
    SELECT source, entered_by_user_id INTO v_source, v_user FROM fx_rates WHERE id = v_rate;
    IF v_source IS DISTINCT FROM 'manual' OR v_user IS NOT NULL THEN
        RAISE EXCEPTION 'FAIL: after its author was deleted the rate read % by %, expected manual by nobody',
            v_source, v_user;
    END IF;

    -- CASE 6 — fx_pair_coverage and benchmark_coverage.
    INSERT INTO fx_pair_coverage (ledger_id, from_currency, to_currency, fetched_from_date, fetched_through_date)
         VALUES (v_ledger, 'EUR', 'USD', DATE '2025-12-29', DATE '2026-01-05');
    BEGIN
        INSERT INTO fx_pair_coverage (ledger_id, from_currency, to_currency, fetched_from_date, fetched_through_date)
             VALUES (v_ledger, 'EUR', 'EUR', DATE '2025-12-29', DATE '2026-01-05');
        RAISE EXCEPTION 'FAIL: coverage of a currency to itself was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_pair_coverage (ledger_id, from_currency, to_currency, fetched_from_date, fetched_through_date)
             VALUES (v_ledger, 'EUR', 'USD', DATE '2025-12-29', DATE '2026-01-06');
        RAISE EXCEPTION 'FAIL: a pair was covered twice';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_pair_coverage (ledger_id, from_currency, to_currency, fetched_from_date, fetched_through_date)
             VALUES (v_ledger, 'GBP', 'USD', DATE '2026-01-06', DATE '2026-01-05');
        RAISE EXCEPTION 'FAIL: a pair range that runs backwards was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO fx_pair_coverage (ledger_id, from_currency, to_currency, fetched_through_date)
             VALUES (v_ledger, 'CHF', 'USD', DATE '2026-01-05');
        RAISE EXCEPTION 'FAIL: a pair range with no from day was stored';
    EXCEPTION WHEN not_null_violation THEN NULL;
    END;

    INSERT INTO benchmark_coverage (ledger_id, symbol, fetched_from_date, fetched_through_date)
         VALUES (v_ledger, 'FNDA', DATE '2025-12-29', DATE '2026-01-05');
    BEGIN
        INSERT INTO benchmark_coverage (ledger_id, symbol, fetched_from_date, fetched_through_date)
             VALUES (v_ledger, 'FNDA', DATE '2025-12-01', DATE '2026-01-05');
        RAISE EXCEPTION 'FAIL: a symbol was covered twice';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO benchmark_coverage (ledger_id, symbol, fetched_from_date, fetched_through_date)
             VALUES (v_ledger, 'FNDB', DATE '2026-01-06', DATE '2026-01-05');
        RAISE EXCEPTION 'FAIL: a benchmark range that runs backwards was stored';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO benchmark_coverage (ledger_id, symbol, fetched_from_date, fetched_through_date)
             VALUES (v_ledger, '  ', DATE '2026-01-05', DATE '2026-01-05');
        RAISE EXCEPTION 'FAIL: a blank symbol was covered';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    -- A removed benchmark keeps its range: no reference ties the range to the list.
    DELETE FROM ledger_benchmarks WHERE ledger_id = v_ledger AND symbol = 'FNDA';
    IF NOT EXISTS (SELECT 1 FROM benchmark_coverage WHERE ledger_id = v_ledger AND symbol = 'FNDA') THEN
        RAISE EXCEPTION 'FAIL: removing a benchmark removed its coverage';
    END IF;
END $$;

-- CASE 7 — the catalog.
DO $$
DECLARE
    v_table text;
    v_priv  text;
BEGIN
    FOREACH v_table IN ARRAY ARRAY['benchmark_growth', 'fx_rates'] LOOP
        FOREACH v_priv IN ARRAY ARRAY['UPDATE', 'DELETE', 'TRUNCATE'] LOOP
            IF has_table_privilege('coffer_app', v_table, v_priv) THEN
                RAISE EXCEPTION 'FAIL: coffer_app holds % on %', v_priv, v_table;
            END IF;
        END LOOP;
        IF NOT has_table_privilege('coffer_app', v_table, 'INSERT') THEN
            RAISE EXCEPTION 'FAIL: coffer_app cannot insert into %', v_table;
        END IF;
    END LOOP;
    FOREACH v_table IN ARRAY ARRAY['fx_pair_coverage', 'benchmark_coverage'] LOOP
        IF has_table_privilege('coffer_app', v_table, 'DELETE') THEN
            RAISE EXCEPTION 'FAIL: coffer_app holds DELETE on %', v_table;
        END IF;
        IF NOT has_table_privilege('coffer_app', v_table, 'UPDATE') THEN
            RAISE EXCEPTION 'FAIL: coffer_app cannot widen %', v_table;
        END IF;
    END LOOP;

    FOREACH v_table IN ARRAY ARRAY['ledger_benchmarks', 'benchmark_growth', 'benchmark_coverage', 'fx_rates', 'fx_pair_coverage'] LOOP
        IF NOT v_table = ANY (fn_ledger_snapshot_part_names()) THEN
            RAISE EXCEPTION 'FAIL: snapshots do not capture %', v_table;
        END IF;
        IF NOT v_table = ANY (fn_ledger_snapshot_insert_order()) THEN
            RAISE EXCEPTION 'FAIL: snapshot restore does not insert %', v_table;
        END IF;
    END LOOP;
END $$;

ROLLBACK;
