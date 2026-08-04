-- =============================================================================
-- 168 — recurring_transactions.loan_account_id: managed loan-payment reminder
--        link (ADR-0050 extension)
-- =============================================================================
--
-- A loan's scheduled auto-payment is a recurring_transactions row
-- (is_loan_reminder) whose principal/interest/escrow split is computed from
-- loan_terms + the loan account's current balance. That link was only inferred
-- (a series whose template posts to a loan account) and invisible in the UI.
-- Make it first-class so the loan account editor can surface + manage it.
--
-- Direction: recurring_transactions.loan_account_id -> accounts, mirroring
-- source_account_id (mig 125/126). The reverse (accounts -> recurring) would add
-- a cycle to the snapshot-restore insert order (accounts are inserted before
-- recurring_transactions), forcing a DEFERRABLE FK; this direction needs none.
-- A partial unique index enforces one managed reminder per loan account.
--
-- Idempotent: fresh installs + re-runs are no-ops.
-- =============================================================================

ALTER TABLE recurring_transactions
    ADD COLUMN IF NOT EXISTS loan_account_id UUID;

-- Composite, ledger-scoped FK (mig-049/072/126 coherence). ON DELETE RESTRICT:
-- the template legs already pin the loan account. ADD CONSTRAINT has no
-- IF NOT EXISTS, so guard on the catalog.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'recurring_transactions_loan_account_fkey'
    ) THEN
        ALTER TABLE recurring_transactions
            ADD CONSTRAINT recurring_transactions_loan_account_fkey
                FOREIGN KEY (loan_account_id, ledger_id)
                REFERENCES accounts (id, ledger_id)
                ON DELETE RESTRICT;
    END IF;
END $$;

-- At most one managed reminder per loan account.
CREATE UNIQUE INDEX IF NOT EXISTS uq_recurring_loan_account
    ON recurring_transactions (loan_account_id)
    WHERE loan_account_id IS NOT NULL;

-- Backfill: link each existing managed (is_loan_reminder) series to the loan
-- account its template posts to — the inferred link the app used at runtime.
-- If a loan is somehow touched by more than one such series, take the earliest
-- (deterministic) so the unique index holds; the rest stay NULL.
WITH candidates AS (
    SELECT DISTINCT rt.id AS reminder_id, a.id AS loan_account_id, rt.created_at
    FROM recurring_transactions rt
    JOIN txn_legs l ON l.header_id = rt.template_header_id
    JOIN accounts a ON a.id = l.account_id AND a.account_type = 'loan'
    WHERE rt.template_header_id IS NOT NULL
      AND rt.is_loan_reminder = TRUE
),
ranked AS (
    SELECT reminder_id, loan_account_id,
           ROW_NUMBER() OVER (
               PARTITION BY loan_account_id
               ORDER BY created_at ASC, reminder_id ASC
           ) AS rn
    FROM candidates
)
UPDATE recurring_transactions rt
SET loan_account_id = r.loan_account_id
FROM ranked r
WHERE rt.id = r.reminder_id
  AND r.rn = 1
  AND rt.loan_account_id IS DISTINCT FROM r.loan_account_id;
