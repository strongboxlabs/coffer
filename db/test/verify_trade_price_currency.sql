-- Verify migration 274: a trade price is in the currency of the brokerage that made the
-- trade, never of the holdings account its line sits on, and the checker compares that
-- currency.
--
-- Case 1 is the rule: fn_trade_execution_for_day names the picked execution's price, its
-- brokerage's currency and that brokerage, though the holdings account the line sits on
-- kept another code, and fn_trade_price_reseed writes the brokerage's.
-- Case 2 is the checker: a stored import, simplefin or trade row at the day's price in
-- another currency is a value mismatch naming both currencies, and agrees once the
-- writer has written the day.
-- Case 3 is a day one security trades in two brokerages: the currency is the brokerage
-- of the picked execution (last by seq), for the writer and the checker alike.
-- Case 4 is the rank gate: a fetch or a manual price in another currency owns its day
-- and is not compared.
-- Case 5 is the catalog: the four-argument writer, which stored 'USD', is gone, and so
-- is fn_trade_price_for_day, the rule's price alone, which nothing calls.
-- Case 6 is whose currency is read: a line on an account no brokerage names takes that
-- account's, and a holdings account two brokerages name in different codes takes the
-- first code in ordinal order, for the rule, the writer and the checker alike.
-- Case 7 is what the writers lock: fn_trade_price_accounts names, for a day, the
-- accounts whose currency the rule can read whichever execution it picks (brokerages,
-- never a holdings account), and fn_lock_trade_price_accounts locks them, refusing any
-- isolation level but READ COMMITTED; fn_lock_securities, what merge_securities takes on
-- its source, locks the securities it is given, refusing the same.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_trade_price_currency.sql
-- All assertions are plpgsql DO blocks; any failure aborts. Each transaction ends in
-- ROLLBACK.

BEGIN ISOLATION LEVEL REPEATABLE READ;

DO $$
DECLARE
    v_mode boolean;
BEGIN
    FOREACH v_mode IN ARRAY ARRAY[TRUE, FALSE] LOOP
        BEGIN
            PERFORM * FROM fn_lock_trade_price_accounts(ARRAY[]::uuid[], v_mode);
            RAISE EXCEPTION 'FAIL: fn_lock_trade_price_accounts(for_write %) ran under REPEATABLE READ', v_mode;
        EXCEPTION WHEN invalid_transaction_state THEN NULL;
        END;
    END LOOP;
    BEGIN
        PERFORM * FROM fn_lock_securities(ARRAY[]::uuid[]);
        RAISE EXCEPTION 'FAIL: fn_lock_securities ran under REPEATABLE READ';
    EXCEPTION WHEN invalid_transaction_state THEN NULL;
    END;
END $$;

ROLLBACK;

BEGIN;

DO $$
DECLARE
    v_ledger     uuid := gen_random_uuid();
    v_euro_h     uuid := gen_random_uuid();
    v_euro_b     uuid := gen_random_uuid();
    v_pound_h    uuid := gen_random_uuid();
    v_pound_b    uuid := gen_random_uuid();
    v_bare       uuid := gen_random_uuid();
    v_shared_h   uuid := gen_random_uuid();
    v_shared_b1  uuid := gen_random_uuid();
    v_shared_b2  uuid := gen_random_uuid();
    v_fund       uuid := gen_random_uuid();
    v_both       uuid := gen_random_uuid();
    v_owned      uuid := gen_random_uuid();
    v_alone      uuid := gen_random_uuid();
    v_shared     uuid := gen_random_uuid();
    v_day        date := DATE '2025-03-03';
    v_h_buy      uuid := gen_random_uuid();
    v_h_first    uuid := gen_random_uuid();
    v_h_second   uuid := gen_random_uuid();
    v_h_owned    uuid := gen_random_uuid();
    v_h_alone    uuid := gen_random_uuid();
    v_h_shared   uuid := gen_random_uuid();
    v_price      numeric;
    v_currency   text;
    v_account    uuid;
    v_locked_set uuid[];
    v_reason     text;
    v_expected   text;
    v_stored     text;
    v_source     text;
BEGIN
    INSERT INTO ledgers (id, name) VALUES (v_ledger, 'Trade Price Currency Test');
    -- The euro brokerage's holdings account kept the dollar code an edit before 274 left;
    -- the pound pair agrees. v_bare is an account no brokerage names, and v_shared_h a
    -- holdings account two brokerages name in different codes.
    INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                          opening_balance, is_active, is_system)
         VALUES (v_euro_h,   v_ledger, 'Brokerage A Holdings', 'investment', 'USD', 0, TRUE, TRUE),
                (v_pound_h,  v_ledger, 'Brokerage B Holdings', 'investment', 'GBP', 0, TRUE, TRUE),
                (v_bare,     v_ledger, 'Account C',            'investment', 'CHF', 0, TRUE, FALSE),
                (v_shared_h, v_ledger, 'Shared Holdings',      'investment', 'USD', 0, TRUE, TRUE);
    INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                          opening_balance, is_active, is_system, holdings_account_id)
         VALUES (v_euro_b,    v_ledger, 'Brokerage A', 'investment', 'EUR', 0, TRUE, FALSE, v_euro_h),
                (v_pound_b,   v_ledger, 'Brokerage B', 'investment', 'GBP', 0, TRUE, FALSE, v_pound_h),
                (v_shared_b1, v_ledger, 'Brokerage D', 'investment', 'JPY', 0, TRUE, FALSE, v_shared_h),
                (v_shared_b2, v_ledger, 'Brokerage E', 'investment', 'CAD', 0, TRUE, FALSE, v_shared_h);
    INSERT INTO securities (id, ledger_id, ticker, name, is_active, share_decimals)
         VALUES (v_fund,   v_ledger, 'FNDA', 'Fund A', TRUE, 4),
                (v_both,   v_ledger, 'FNDB', 'Fund B', TRUE, 4),
                (v_owned,  v_ledger, 'FNDC', 'Fund C', TRUE, 4),
                (v_alone,  v_ledger, 'FNDD', 'Fund D', TRUE, 4),
                (v_shared, v_ledger, 'FNDE', 'Fund E', TRUE, 4);

    -- CASE 1 — a buy on the euro brokerage's holdings account, which kept the dollar code.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_buy, v_ledger, 'manual', 'buy', 'buy', v_day, v_day, v_day);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_buy, v_ledger, v_euro_h, 0,
                 250.00, v_fund, 10, 25.00, 'security');

    SELECT e.price, e.currency_code, e.account_id INTO v_price, v_currency, v_account
      FROM fn_trade_execution_for_day(v_ledger, v_fund, v_day) e;
    IF v_price IS DISTINCT FROM 25.0000 OR v_currency IS DISTINCT FROM 'EUR'
       OR v_account IS DISTINCT FROM v_euro_b THEN
        RAISE EXCEPTION 'The rule named % % from % (expected 25.0000 EUR from the brokerage)',
            v_price, v_currency, v_account;
    END IF;

    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_fund, v_day);
    SELECT p.price, p.currency_code, p.source INTO v_price, v_currency, v_source
      FROM security_prices p WHERE p.security_id = v_fund AND p.price_date = v_day;
    IF v_price IS DISTINCT FROM 25.0000 OR v_currency IS DISTINCT FROM 'EUR'
       OR v_source IS DISTINCT FROM 'trade' THEN
        RAISE EXCEPTION 'The writer stored % % % (expected 25.0000 EUR trade)',
            v_price, v_currency, v_source;
    END IF;

    -- CASE 2 — the same day stored in dollars at the same price, from each source the
    -- checker owns: a value mismatch on the currency alone.
    FOREACH v_source IN ARRAY ARRAY['trade', 'import', 'simplefin'] LOOP
        UPDATE security_prices SET currency_code = 'USD', source = v_source
         WHERE security_id = v_fund AND price_date = v_day;
        SELECT c.reason, c.expected_currency, c.stored_currency INTO v_reason, v_expected, v_stored
          FROM fn_trade_price_check(v_ledger) c
         WHERE c.security_id = v_fund AND c.price_date = v_day;
        IF v_reason IS DISTINCT FROM 'value' OR v_expected IS DISTINCT FROM 'EUR'
           OR v_stored IS DISTINCT FROM 'USD' THEN
            RAISE EXCEPTION 'A % row in another currency read reason %, expected %, stored %',
                v_source, v_reason, v_expected, v_stored;
        END IF;

        PERFORM * FROM fn_trade_price_reseed(v_ledger, v_fund, v_day);
        SELECT c.reason INTO v_reason
          FROM fn_trade_price_check(v_ledger) c
         WHERE c.security_id = v_fund AND c.price_date = v_day;
        IF v_reason IS NOT NULL THEN
            RAISE EXCEPTION 'The checker disagreed with the writer after a % row: %', v_source, v_reason;
        END IF;
    END LOOP;

    -- CASE 3 — one security bought on one day in both brokerages, the pound one later by
    -- seq: the day is the pound execution's, in pounds.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_first, v_ledger, 'manual', 'buy', 'buy', v_day, v_day, v_day);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_first, v_ledger, v_euro_h, 0,
                 300.00, v_both, 10, 30.00, 'security');
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_second, v_ledger, 'manual', 'buy', 'buy', v_day, v_day, v_day);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_second, v_ledger, v_pound_h, 0,
                 260.00, v_both, 10, 26.00, 'security');
    IF (SELECT seq FROM txn_headers WHERE id = v_h_second)
       <= (SELECT seq FROM txn_headers WHERE id = v_h_first) THEN
        RAISE EXCEPTION 'Premise: the pound execution must sort later by seq';
    END IF;

    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_both, v_day);
    SELECT p.price, p.currency_code INTO v_price, v_currency
      FROM security_prices p WHERE p.security_id = v_both AND p.price_date = v_day;
    IF v_price IS DISTINCT FROM 26.0000 OR v_currency IS DISTINCT FROM 'GBP' THEN
        RAISE EXCEPTION 'A day traded in two brokerages stored % % (expected 26.0000 GBP)',
            v_price, v_currency;
    END IF;
    SELECT c.reason, c.expected_currency INTO v_reason, v_expected
      FROM fn_trade_price_check(v_ledger) c
     WHERE c.security_id = v_both AND c.price_date = v_day;
    IF v_reason IS NOT NULL OR v_expected IS DISTINCT FROM 'GBP' THEN
        RAISE EXCEPTION 'The checker read reason %, expected % for a day traded in two brokerages',
            v_reason, v_expected;
    END IF;

    -- CASE 4 — a fetch and a manual price in dollars on a euro trade's day are not
    -- compared, and the writer leaves them as they are.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_owned, v_ledger, 'manual', 'buy', 'buy', v_day, v_day, v_day);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_owned, v_ledger, v_euro_h, 0,
                 400.00, v_owned, 10, 40.00, 'security');
    INSERT INTO security_prices (id, ledger_id, security_id, price, currency_code,
                                 price_date, source)
         VALUES (gen_random_uuid(), v_ledger, v_owned, 40.0000, 'USD', v_day, 'fetch');
    FOREACH v_source IN ARRAY ARRAY['fetch', 'manual'] LOOP
        UPDATE security_prices SET source = v_source
         WHERE security_id = v_owned AND price_date = v_day;
        SELECT c.reason INTO v_reason
          FROM fn_trade_price_check(v_ledger) c
         WHERE c.security_id = v_owned AND c.price_date = v_day;
        IF v_reason IS NOT NULL THEN
            RAISE EXCEPTION 'A % price in another currency was reported: %', v_source, v_reason;
        END IF;
        PERFORM * FROM fn_trade_price_reseed(v_ledger, v_owned, v_day);
        SELECT p.currency_code, p.source INTO v_currency, v_stored
          FROM security_prices p WHERE p.security_id = v_owned AND p.price_date = v_day;
        IF v_currency IS DISTINCT FROM 'USD' OR v_stored IS DISTINCT FROM v_source THEN
            RAISE EXCEPTION 'The writer overwrote a % price: now % %', v_source, v_currency, v_stored;
        END IF;
    END LOOP;

    -- CASE 5 — the writer that took no currency is gone, and the one that takes it is
    -- there to be found.
    IF to_regprocedure('security_price_upsert_from_trade(uuid, uuid, date, numeric)') IS NOT NULL THEN
        RAISE EXCEPTION 'The four-argument security_price_upsert_from_trade still exists';
    END IF;
    IF to_regprocedure('security_price_upsert_from_trade(uuid, uuid, date, numeric, text)') IS NULL THEN
        RAISE EXCEPTION 'The five-argument security_price_upsert_from_trade does not exist';
    END IF;
    -- The rule's price alone is gone, and the rule itself is there to be found.
    IF to_regprocedure('fn_trade_price_for_day(uuid, uuid, date)') IS NOT NULL THEN
        RAISE EXCEPTION 'fn_trade_price_for_day still exists, though nothing calls it';
    END IF;
    IF to_regprocedure('fn_trade_execution_for_day(uuid, uuid, date)') IS NULL THEN
        RAISE EXCEPTION 'fn_trade_execution_for_day does not exist';
    END IF;

    -- CASE 6 — a line on an account no brokerage names takes that account's currency.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_alone, v_ledger, 'manual', 'buy', 'buy', v_day, v_day, v_day);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_alone, v_ledger, v_bare, 0,
                 150.00, v_alone, 10, 15.00, 'security');
    SELECT e.currency_code, e.account_id INTO v_currency, v_account
      FROM fn_trade_execution_for_day(v_ledger, v_alone, v_day) e;
    IF v_currency IS DISTINCT FROM 'CHF' OR v_account IS DISTINCT FROM v_bare THEN
        RAISE EXCEPTION 'A line on an account no brokerage names read % from % (expected CHF from it)',
            v_currency, v_account;
    END IF;

    -- A holdings account two brokerages name, in JPY and CAD: CAD, the first in ordinal
    -- order, for the rule, the writer and the checker.
    INSERT INTO txn_headers (id, ledger_id, origin, action, payee,
                             posted_at, transacted_at, created_at)
         VALUES (v_h_shared, v_ledger, 'manual', 'buy', 'buy', v_day, v_day, v_day);
    INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index,
                          amount, security_id, quantity, unit_price, posting_role)
         VALUES (gen_random_uuid(), v_h_shared, v_ledger, v_shared_h, 0,
                 180.00, v_shared, 10, 18.00, 'security');
    SELECT e.currency_code, e.account_id INTO v_currency, v_account
      FROM fn_trade_execution_for_day(v_ledger, v_shared, v_day) e;
    IF v_currency IS DISTINCT FROM 'CAD' OR v_account IS DISTINCT FROM v_shared_b2 THEN
        RAISE EXCEPTION 'A holdings account two brokerages name read % from % (expected CAD)',
            v_currency, v_account;
    END IF;
    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_shared, v_day);
    PERFORM * FROM fn_trade_price_reseed(v_ledger, v_alone, v_day);
    IF EXISTS (SELECT 1 FROM fn_trade_price_check(v_ledger) c
                WHERE c.security_id IN (v_alone, v_shared) AND c.reason IS NOT NULL) THEN
        RAISE EXCEPTION 'The checker disagreed with the writer on whose currency is read';
    END IF;
    IF (SELECT count(*) FROM security_prices p
         WHERE (p.security_id = v_alone AND p.currency_code = 'CHF')
            OR (p.security_id = v_shared AND p.currency_code = 'CAD')) <> 2 THEN
        RAISE EXCEPTION 'The writer did not store CHF and CAD for the two days';
    END IF;

    -- CASE 7 — the accounts the writers lock: for the day traded in both brokerages, the
    -- two brokerages and not their holdings accounts; for the shared holdings account,
    -- both brokerages that name it; for the line no brokerage names, its account.
    SELECT array_agg(t.account_id ORDER BY t.account_id) INTO v_locked_set
      FROM fn_trade_price_accounts(v_ledger, ARRAY[v_both], ARRAY[v_day]) t;
    IF v_locked_set IS DISTINCT FROM (SELECT array_agg(x ORDER BY x) FROM unnest(ARRAY[v_euro_b, v_pound_b]) x) THEN
        RAISE EXCEPTION 'A day traded in two brokerages names % to lock', v_locked_set;
    END IF;
    SELECT array_agg(t.account_id ORDER BY t.account_id) INTO v_locked_set
      FROM fn_trade_price_accounts(v_ledger, ARRAY[v_shared, v_alone], ARRAY[v_day, v_day]) t;
    IF v_locked_set IS DISTINCT FROM
       (SELECT array_agg(x ORDER BY x) FROM unnest(ARRAY[v_shared_b1, v_shared_b2, v_bare]) x) THEN
        RAISE EXCEPTION 'The shared holdings day and the bare account''s day name % to lock', v_locked_set;
    END IF;
    IF (SELECT count(*) FROM fn_trade_price_accounts(v_ledger, ARRAY[v_both], ARRAY[v_day + 1])) <> 0 THEN
        RAISE EXCEPTION 'A day with no execution names accounts to lock';
    END IF;
    IF (SELECT l.locked FROM fn_lock_trade_price_accounts(v_locked_set, TRUE) l) <> 3
       OR (SELECT l.locked FROM fn_lock_trade_price_accounts(v_locked_set, FALSE) l) <> 3 THEN
        RAISE EXCEPTION 'fn_lock_trade_price_accounts did not lock the three accounts in both modes';
    END IF;
    IF (SELECT l.locked FROM fn_lock_securities(ARRAY[v_both, v_shared, gen_random_uuid()]) l) <> 2 THEN
        RAISE EXCEPTION 'fn_lock_securities did not lock the two securities it was given that exist';
    END IF;

    RAISE NOTICE 'verify_trade_price_currency: 7 cases OK';
END;
$$;

ROLLBACK;
