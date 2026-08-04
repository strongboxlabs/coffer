-- 043_investment_action_cleanup_and_precision_bump.sql
--
-- Pre-A1 cleanup: align the investment-action CHECK constraint with the
-- agreed user-facing action set, and bump share-quantity / per-unit-price
-- precision to support MD-fidelity fractional shares (real exports show
-- 11-decimal share counts on reinvested-dividend lots; today's (19,6)
-- silently rounds).
--
-- =============================================================================
-- 1) investment_action: 12 -> 9 actions
-- =============================================================================
-- Current CHECK (after migration 024) is the union of legacy MD shapes
-- and the per-leg directional split (transfer_in/transfer_out, fee, etc.).
-- ADR-0019's symmetric postings already encode direction in the leg's
-- amount sign; the directional action labels are redundant.
--
-- Final 9-action set:
--   buy, sell, dividend_cash, dividend_reinvest, interest,
--   transfer, misc_income, misc_expense, split
--
-- Audit on dev (real MD export, 41K legs) showed only:
--   dividend_reinvest 18624, buy 14554, sell 4752, transfer_in 1304,
--   transfer_out 1274, dividend_cash 1032, misc_income 36.
-- Zero rows of interest / contribution / withdrawal / fee.
-- So the only backfill needed is transfer_in/_out -> transfer.
--
-- Order matters: drop the CHECK first so the backfill UPDATE isn't
-- rejected by the still-active old constraint, THEN re-add the CHECK
-- once the data is clean.

BEGIN;

ALTER TABLE txn_legs DROP CONSTRAINT IF EXISTS txn_legs_investment_action_check;

UPDATE txn_legs
   SET investment_action = 'transfer'
 WHERE investment_action IN ('transfer_in', 'transfer_out');

ALTER TABLE txn_legs
    ADD CONSTRAINT txn_legs_investment_action_check
    CHECK (investment_action IS NULL OR investment_action IN (
        'buy', 'sell',
        'dividend_cash', 'dividend_reinvest',
        'interest',
        'transfer',
        'misc_income', 'misc_expense',
        'split'
    ));

-- =============================================================================
-- 2) Precision bumps for share quantities and per-unit prices
-- =============================================================================
-- Money columns stay at NUMERIC(19, 4) (currency precision is sufficient
-- and the running-balance trigger / cost-basis math assume 4-scale).
--
-- Share-quantity columns and per-unit-price columns move to NUMERIC(25, 12):
--   - 25-digit precision: ample headroom over real-world holdings sizes.
--   - 12-decimal scale: matches MD's 11-decimal display with one digit of
--     buffer; covers cash ÷ price reinvest math without rounding loss.
--
-- Affected columns:
--   txn_legs.quantity        (19,8) -> (25,12)
--   txn_legs.unit_price      (19,8) -> (25,12)
--   holdings.quantity        (19,6) -> (25,12)
--   lots.quantity            (19,6) -> (25,12)
--   security_prices.price    (19,4) -> (25,12)  -- T-Bill at 0.98082 has
--                                                  5 decimals; (19,4) was
--                                                  already lossy for these.

ALTER TABLE txn_legs        ALTER COLUMN quantity   TYPE NUMERIC(25, 12);
ALTER TABLE txn_legs        ALTER COLUMN unit_price TYPE NUMERIC(25, 12);
ALTER TABLE holdings        ALTER COLUMN quantity   TYPE NUMERIC(25, 12);
ALTER TABLE lots            ALTER COLUMN quantity   TYPE NUMERIC(25, 12);
ALTER TABLE security_prices ALTER COLUMN price      TYPE NUMERIC(25, 12);

COMMIT;
