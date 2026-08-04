-- Verify the mig-156 CHECK ck_securities_quote_symbol_public (ADR-0054 D2).
-- The rule: a security may be marked NOT public only when it has a quote
-- symbol to keep private — a bare ticker is always public. The API layer
-- (SecuritiesRepository Create/Patch) enforces the same rule for a clean 422,
-- but this is the DB backstop: if the API guard is ever bypassed or removed,
-- the CHECK must still reject the bad state. That guarantee only has teeth if
-- something actually tries to violate it, so this script does.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_quote_symbol_public_check.sql
-- All assertions use plpgsql DO blocks; any failure aborts the script.
-- The ROLLBACK at the end discards the fixture.

BEGIN;

INSERT INTO ledgers (id, name)
VALUES ('cccccccc-0000-0000-0000-cccccccccccc', 'QSP Test Ledger');

-- Test 1 (INSERT reject): not public + no quote symbol must violate the CHECK.
DO $$
BEGIN
    BEGIN
        INSERT INTO securities (ledger_id, name, quote_symbol, quote_symbol_public)
        VALUES ('cccccccc-0000-0000-0000-cccccccccccc',
                'Bad: not public, no symbol', NULL, false);
        RAISE EXCEPTION
            'FAIL: ck_securities_quote_symbol_public allowed (not public + no quote symbol)';
    EXCEPTION
        WHEN check_violation THEN
            RAISE NOTICE 'OK: INSERT of not-public-without-symbol was rejected';
    END;
END $$;

-- Test 2 (allow): not public WITH a quote symbol — the 529 feed-only case.
INSERT INTO securities (id, ledger_id, name, quote_symbol, quote_symbol_public)
VALUES ('cccccccc-2222-2222-2222-cccccccccccc',
        'cccccccc-0000-0000-0000-cccccccccccc',
        'OK: private feed symbol', '8918', false);

-- Test 3 (allow): public with no quote symbol — a bare ticker (default state).
INSERT INTO securities (id, ledger_id, name, ticker, quote_symbol, quote_symbol_public)
VALUES ('cccccccc-3333-3333-3333-cccccccccccc',
        'cccccccc-0000-0000-0000-cccccccccccc',
        'OK: public ticker only', 'ETFA', NULL, true);

-- Test 4 (UPDATE reject): flipping the public ticker-only row to not-public
-- while it still has no quote symbol must also violate the CHECK (the API
-- Patch guard mirrors this).
DO $$
BEGIN
    BEGIN
        UPDATE securities
           SET quote_symbol_public = false
         WHERE id = 'cccccccc-3333-3333-3333-cccccccccccc';
        RAISE EXCEPTION
            'FAIL: ck_securities_quote_symbol_public allowed UPDATE to not-public with no symbol';
    EXCEPTION
        WHEN check_violation THEN
            RAISE NOTICE 'OK: UPDATE to not-public-without-symbol was rejected';
    END;
END $$;

ROLLBACK;
