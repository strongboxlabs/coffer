-- =============================================================================
-- 062 — Enact the Ledger investment action catalog (ADR-0027 / A4.b)
-- =============================================================================
--
-- ADR-0027 locked the 9-action Ledger set:
--   buy, buyx, sell, sellx, dividend_cash, dividend_reinvest, divx,
--   transfer, misc
--
-- The pre-A4 set in production was:
--   buy, sell, dividend_cash, dividend_reinvest, interest, transfer,
--   misc_income, misc_expense
--
-- Plus the importer was FLATTENING MD's compound `buyx`/`sellx`/`divx`
-- txntypes to `buy`/`sell`/`dividend_cash` with extra postings — this
-- left 558 rows in the dev DB carrying "hidden compound" shapes
-- (e.g. action=buy with a {security, transfer} posting set, which is
-- structurally a buyx). Another 742 rows carried off-catalog shapes
-- that the importer's old discriminator misclassified.
--
-- This migration enacts ADR-0027 in two steps:
--
--   1. **Wipe investment data.** DELETE every investment-tagged row
--      (txn_headers with action IS NOT NULL, their legs via cascade,
--      and all holdings / lots / security_splits). Non-investment
--      txns (bank payments, manual splits) are untouched.
--
--   2. **Update the CHECK constraint** on txn_headers.action: drop
--      `interest`, `misc_income`, `misc_expense`; add `buyx`,
--      `sellx`, `divx`, `misc`. Final set = the 9 from ADR-0027.
--
-- Re-import (companion to this migration): once applied, the user
-- runs the Moneydance importer again. The fixed discriminator stamps
-- the new action codes natively; holdings / lots / security_splits
-- are repopulated from the wire-form MD data.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Part 1: wipe investment data.
--
-- Order respects FK dependencies:
--   security_splits → no FK from investment headers; deletable first
--   lots            → FK to txn_legs(id); delete before legs
--   holdings        → no FK from txn_legs; standalone
--   txn_legs        → cascades on txn_headers delete (FK ON DELETE CASCADE)
--   txn_headers     → root
-- -----------------------------------------------------------------------------

-- security_splits is metadata for B0.7 stock-splits; all rows wipe on
-- re-import (the importer re-creates from MD csplit objects).
DELETE FROM security_splits;

-- lots reference txn_legs; clear before legs go.
DELETE FROM lots;

-- holdings has no FK from legs/headers; clear wholesale.
DELETE FROM holdings;

-- txn_legs cascades from txn_headers, but explicit DELETE first avoids
-- a CASCADE of arbitrary size and makes the wipe auditable.
DELETE FROM txn_legs
 WHERE header_id IN (SELECT id FROM txn_headers WHERE action IS NOT NULL);

-- Finally the investment headers themselves.
DELETE FROM txn_headers WHERE action IS NOT NULL;


-- -----------------------------------------------------------------------------
-- Part 2: update the action CHECK constraint to the ADR-0027 set.
-- -----------------------------------------------------------------------------

ALTER TABLE txn_headers DROP CONSTRAINT txn_headers_action_check;
ALTER TABLE txn_headers
    ADD CONSTRAINT txn_headers_action_check
    CHECK (action IS NULL OR action IN (
        'buy', 'buyx',
        'sell', 'sellx',
        'dividend_cash', 'dividend_reinvest', 'divx',
        'transfer',
        'misc'
    ));


-- -----------------------------------------------------------------------------
-- Verification: no investment headers remain; new CHECK accepts the
-- 9 actions from ADR-0027 and rejects the dropped ones.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_remaining_investment_headers INTEGER;
BEGIN
    SELECT COUNT(*) INTO v_remaining_investment_headers
    FROM txn_headers WHERE action IS NOT NULL;
    IF v_remaining_investment_headers > 0 THEN
        RAISE EXCEPTION 'Migration 062: % investment header(s) survived the wipe.', v_remaining_investment_headers;
    END IF;
END;
$$;
