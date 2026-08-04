-- 024_widen_txn_legs_investment_action.sql
--
-- Migration 022 cloned the investment_action CHECK list from migration
-- 002, but migration 007 had already widened the list on the legacy
-- `transactions` table to include 'interest' and 'misc_income' (MD
-- exports carry txns of these flavours that don't fit the original
-- nine actions). The investment importer fails on 'misc_income' legs
-- under the narrower list.
--
-- Re-align txn_legs.investment_action to the same widened list. No
-- data movement; just DROP + ADD the constraint.

ALTER TABLE txn_legs DROP CONSTRAINT txn_legs_investment_action_check;

ALTER TABLE txn_legs
    ADD CONSTRAINT txn_legs_investment_action_check
    CHECK (investment_action IS NULL OR investment_action IN (
        'buy', 'sell',
        'dividend_cash', 'dividend_reinvest',
        'interest', 'misc_income',
        'contribution', 'withdrawal',
        'split',
        'transfer_in', 'transfer_out',
        'fee'
    ));
