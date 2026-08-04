-- =============================================================================
-- 126 — recurring_transactions.source_account_id catch-up (ADR-0047 / ADR-0049)
-- =============================================================================
--
-- mig 125 (as merged in #202) adds recurring_transactions.source_account_id -
-- the display/query pointer that drives the reminders agenda amount. During
-- #202's draft phase, however, a dev database applied an EARLIER version of
-- 125 that did NOT yet have that column. DbUp journals scripts by FILENAME
-- (__schema_migrations), so it will not re-run 125 on that database, leaving it
-- without the column the API now queries.
--
-- This forward migration reconciles such a database through DbUp (the only
-- sanctioned path - never an out-of-band ALTER). It is fully IDEMPOTENT: on any
-- database that already has the column + FK from the merged 125 (fresh installs
-- and CI), both statements are no-ops. The end schema is identical either way.
-- =============================================================================

ALTER TABLE recurring_transactions
    ADD COLUMN IF NOT EXISTS source_account_id UUID;

-- Composite, ledger-scoped FK (mig-049/072 coherence pattern) - ON DELETE
-- RESTRICT (the template legs already pin the account). ADD CONSTRAINT has no
-- IF NOT EXISTS, so guard on the catalog.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'recurring_transactions_source_account_fkey'
    ) THEN
        ALTER TABLE recurring_transactions
            ADD CONSTRAINT recurring_transactions_source_account_fkey
                FOREIGN KEY (source_account_id, ledger_id)
                REFERENCES accounts (id, ledger_id)
                ON DELETE RESTRICT;
    END IF;
END $$;
