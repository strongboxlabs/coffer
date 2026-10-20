-- Phase 2 PR 2.6.6: capture metadata the importer was parsing but silently
-- dropping. Two real bugs and a batch of "would be nice" account metadata
-- the user wants persisted before the UI surfaces.
--
-- Bugs:
--   1. `securities.share_decimals` — Moneydance assigns a per-security
--      decimal precision (`dec`) for share quantities. The importer's
--      `MdCurr.Decimals` was extracted but discarded; the investment mapper
--      hardcoded a divisor of 10^4. Mutual funds with `dec=5` had their
--      share counts silently 10× wrong. Per-security precision now lives
--      on the row and the mapper looks it up.
--   2. `transactions.check_number` — paper-check transactions in MD carry
--      a `chk` value visible in the user's register. The importer parsed
--      it (`MdTxn.CheckNumber`) but had nowhere to put it.
--
-- Account metadata previously dropped: hide-in-UI flag, user notes, the
-- account-number / institution-name / routing-number fields the user fills
-- in MD's account-edit dialog, and the institution URL. Useful both for
-- preserving import fidelity and for matching SimpleFIN feeds in Phase 5.
--
-- Pure DDL — no data transformation. Existing rows take the column
-- defaults; the importer re-runs in Phase 2 to backfill from the export.

-- ---------------------------------------------------------------------------
-- 1) securities.share_decimals (per-security share precision)
-- ---------------------------------------------------------------------------
ALTER TABLE securities
    ADD COLUMN share_decimals INTEGER NOT NULL DEFAULT 4
        CHECK (share_decimals BETWEEN 0 AND 6);

COMMENT ON COLUMN securities.share_decimals IS
    'Number of decimal places used for share quantities of this security '
    '(Moneydance''s `dec` field). Stocks typically 4; mutual funds typically '
    '5. Bounded by holdings.quantity scale (NUMERIC(19,6)); raise both '
    'together if higher-precision securities appear.';

-- ---------------------------------------------------------------------------
-- 2) transactions.check_number
-- ---------------------------------------------------------------------------
ALTER TABLE transactions
    ADD COLUMN check_number TEXT;

COMMENT ON COLUMN transactions.check_number IS
    'Paper-check number (Moneydance''s `chk` field). NULL for non-check '
    'transactions and for feeds that don''t supply one.';

-- ---------------------------------------------------------------------------
-- 3) accounts metadata previously dropped on import
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    ADD COLUMN is_hidden          BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN notes              TEXT,
    ADD COLUMN account_number     TEXT,
    ADD COLUMN institution_name   TEXT,
    ADD COLUMN routing_number     TEXT,
    ADD COLUMN account_url        TEXT;

COMMENT ON COLUMN accounts.is_hidden IS
    'User chose to hide this account from default UI lists. Orthogonal to '
    'is_active (which means "open"): a closed account can be visible for '
    'historical lookup, and an active account can be hidden for clutter '
    'reasons. Maps to Moneydance''s `hide` field.';

COMMENT ON COLUMN accounts.notes IS
    'User-authored notes on the account. Maps to Moneydance''s `comment` '
    'field.';

COMMENT ON COLUMN accounts.account_number IS
    'Account number at the institution. Sourced from `bank_account_number` '
    'on bank/credit accounts, `invst_account_number` on investment accounts.';

COMMENT ON COLUMN accounts.institution_name IS
    'Name of the institution holding this account. Sourced from `bank_name` '
    'or `inst_name` in MD; falls back to whichever is non-empty.';

COMMENT ON COLUMN accounts.routing_number IS
    'ACH/OFX bank routing number. Sourced from MD''s `ofx_bank_id`. Useful '
    'for matching SimpleFIN feeds to imported accounts.';

COMMENT ON COLUMN accounts.account_url IS
    'Institution''s website URL. Sourced from MD''s `account_url`.';
