-- =============================================================================
-- 089 — txn_header_account_balances (ADR-0034 part 1)
-- =============================================================================
--
-- Per ADR-0034, the running balance moves from txn_legs.balance_after to a
-- new per-(header, account) table. This migration creates the table and
-- its RLS + grants; migration 090 populates it via the new trigger family.
--
-- WHY A NEW TABLE
--
-- A header touches N accounts; each account sees its own net cash effect
-- from that header and has its own running balance. Per-(header, account)
-- is the natural grain. Storing on txn_legs forced ADR-0028's
-- MAX(posting_index) picker rule to disambiguate which leg's balance to
-- display — load-bearing complexity that the new table makes disappear.
--
-- SHAPE
--
-- (header_id, account_id) is the primary key. ledger_id is denormalized
-- (mig 049 / 071 pattern) so RLS uses direct ledger_id matching instead
-- of two-hop recursion. Composite FKs lock ledger coherence at the DB.
-- =============================================================================

CREATE TABLE txn_header_account_balances (
    header_id      UUID           NOT NULL,
    account_id     UUID           NOT NULL,
    ledger_id      UUID           NOT NULL,
    balance_after  NUMERIC(19, 4) NOT NULL,
    PRIMARY KEY (header_id, account_id),

    -- Single-column FK for ledgers (no composite invariant needed there).
    CONSTRAINT thab_ledger_id_fkey
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT,

    -- Composite FKs to enforce same-ledger invariant (ADR mig 049 pattern).
    -- ON DELETE CASCADE on header — when a header goes, its per-account
    -- balance rows go with it. ON DELETE RESTRICT on account because
    -- accounts holding balances are not deletable while balances exist.
    CONSTRAINT thab_header_fkey
        FOREIGN KEY (header_id, ledger_id)
        REFERENCES txn_headers (id, ledger_id) ON DELETE CASCADE,
    CONSTRAINT thab_account_fkey
        FOREIGN KEY (account_id, ledger_id)
        REFERENCES accounts (id, ledger_id) ON DELETE RESTRICT
);

COMMENT ON TABLE txn_header_account_balances IS
    'Per-(header, account) running balance after the header is applied. '
    'Maintained by the header-walk trigger family (ADR-0034 / mig 090). '
    'Replaces the leg-level balance_after column dropped in mig 092.';

-- "All balance rows for this account, in header order" — drives both the
-- register view's JOIN and the trigger's anchor query.
-- Includes ledger_id so RLS-filtered scans can use the same index.
CREATE INDEX idx_thab_account_ledger
    ON txn_header_account_balances (account_id, ledger_id);

-- -----------------------------------------------------------------------------
-- RLS — direct ledger_id match (mig 071 pattern).
-- -----------------------------------------------------------------------------

ALTER TABLE txn_header_account_balances ENABLE ROW LEVEL SECURITY;

CREATE POLICY txn_header_account_balances_per_user
    ON txn_header_account_balances
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE
    ON txn_header_account_balances
    TO coffer_app, coffer_service;
