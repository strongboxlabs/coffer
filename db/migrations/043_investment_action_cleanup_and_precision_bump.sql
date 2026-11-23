-- 043_investment_action_cleanup_and_precision_bump.sql
--
-- Pre-A1 cleanup: align the investment-action CHECK constraint with the
-- user-facing action set (ADR-0027), and bump share-quantity / per-unit-price
-- precision to support MD-fidelity fractional shares (a reinvested
-- dividend buys cash ÷ price shares, a quotient that rarely ends at six
-- places; today's (19,6) silently rounds whatever finer count the source
-- carries).
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
-- The only retired labels with a successor are transfer_in/_out, so the
-- only backfill is transfer_in/_out -> transfer. The others have no
-- mapping: a row still carrying one fails the re-added CHECK below, and
-- the migration aborts rather than relabelling it by guess.
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
--   - 25-digit precision: 13 integer digits, ample headroom for any
--     holding size.
--   - 12-decimal scale: holds a share count or per-unit price to twelve
--     places, so cash ÷ price reinvest math is stored without the
--     rounding a six-place column applies.
--
-- Affected columns:
--   txn_legs.quantity        (19,8) -> (25,12)
--   txn_legs.unit_price      (19,8) -> (25,12)
--   holdings.quantity        (19,6) -> (25,12)
--   lots.quantity            (19,6) -> (25,12)
--   security_prices.price    (19,4) -> (25,12)  -- a T-Bill priced below par
--                                                  carries 5 decimals; (19,4)
--                                                  was already lossy for these.

ALTER TABLE txn_legs        ALTER COLUMN quantity   TYPE NUMERIC(25, 12);
ALTER TABLE txn_legs        ALTER COLUMN unit_price TYPE NUMERIC(25, 12);
ALTER TABLE holdings        ALTER COLUMN quantity   TYPE NUMERIC(25, 12);
ALTER TABLE lots            ALTER COLUMN quantity   TYPE NUMERIC(25, 12);
ALTER TABLE security_prices ALTER COLUMN price      TYPE NUMERIC(25, 12);

COMMIT;
