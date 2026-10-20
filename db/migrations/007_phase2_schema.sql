-- Phase 2 prep: schema refinements informed by inspecting the real Moneydance
-- export. Captured in ADRs 0016 and 0017. No data exists yet (the importer
-- hasn't run), so this is pure DDL — no data migration is required or
-- attempted.
--
-- Changes:
--   * `accounts`
--       - drop `is_placeholder` (derived in the UI from "has children +
--         no own transactions"; storing it adds nothing the UI can't
--         compute).
--       - replace `account_type` enum: drop 'income'/'expense', add a
--         single 'category' value plus a new 'loan' value.
--       - add `category_kind` discriminator ('income' | 'expense'),
--         non-null iff account_type = 'category'.
--       - tighten `parent_id`: hierarchy is for categories only.
--       - tighten `feed_connection_id` and `opening_balance`: categories
--         carry no real-account state.
--   * `transactions.investment_action` widened to include 'interest' and
--     'misc_income' (Moneydance has txns of these flavours that don't fit
--     the existing enum).

-- ---------------------------------------------------------------------------
-- accounts: drop is_placeholder
-- ---------------------------------------------------------------------------
ALTER TABLE accounts DROP COLUMN is_placeholder;

-- ---------------------------------------------------------------------------
-- accounts: redefine account_type
-- ---------------------------------------------------------------------------
ALTER TABLE accounts DROP CONSTRAINT accounts_account_type_check;

ALTER TABLE accounts
    ADD CONSTRAINT accounts_account_type_check
    CHECK (account_type IN (
        'bank', 'credit_card', 'investment',
        'asset', 'liability', 'loan',
        'category'
    ));

-- ---------------------------------------------------------------------------
-- accounts: add category_kind discriminator
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    ADD COLUMN category_kind TEXT
    CHECK (category_kind IS NULL OR category_kind IN ('income', 'expense'));

-- category_kind is set IFF account_type = 'category'
ALTER TABLE accounts
    ADD CONSTRAINT accounts_category_kind_consistent
    CHECK (
        (account_type =  'category' AND category_kind IS NOT NULL) OR
        (account_type <> 'category' AND category_kind IS NULL)
    );

-- ---------------------------------------------------------------------------
-- accounts: hierarchy is for categories only
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    ADD CONSTRAINT accounts_parent_only_for_categories
    CHECK (parent_id IS NULL OR account_type = 'category');

-- ---------------------------------------------------------------------------
-- accounts: categories carry no real-account state
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    ADD CONSTRAINT accounts_category_has_no_real_state
    CHECK (
        account_type <> 'category'
        OR (feed_connection_id IS NULL AND opening_balance = 0)
    );

-- ---------------------------------------------------------------------------
-- transactions: widen investment_action
-- ---------------------------------------------------------------------------
ALTER TABLE transactions DROP CONSTRAINT transactions_investment_action_check;

ALTER TABLE transactions
    ADD CONSTRAINT transactions_investment_action_check
    CHECK (investment_action IS NULL OR investment_action IN (
        'buy', 'sell',
        'dividend_cash', 'dividend_reinvest',
        'interest', 'misc_income',
        'contribution', 'withdrawal',
        'split',
        'transfer_in', 'transfer_out',
        'fee'
    ));
