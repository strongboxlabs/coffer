# 0078 — Managed loan-payment reminder

Status: Accepted
Date: 2026-07-16
Relates: [ADR-0047](0047-reminders-recurring-transactions.md), [ADR-0050](0050-account-editor-and-loan-amortization.md), [ADR-0034](0034-header-walk-running-balance.md), [ADR-0037](0037-snapshots-and-backups.md)

## Context

A loan's scheduled auto-payment is a recurring reminder (ADR-0047) whose
principal/interest/escrow split is computed live from `loan_terms` + the loan
account's current balance (ADR-0050). Two gaps surfaced on a real imported
mortgage:

1. The reminder↔loan relationship existed only by **inference** (a series whose
   template posts to a loan account) and was set exclusively by the Moneydance
   import — invisible and unmanageable in the UI.
2. The split's "balance owed" was **re-summed from raw `txn_legs`**, which
   diverged from the register's canonical balance (it double-counted
   `is_merged_into` duplicates and ignored leg overrides), throwing off the
   interest/principal division.

## Decisions

### D1 — First-class link: `recurring_transactions.loan_account_id`

The managed reminder points at its loan account via a new nullable, ledger-scoped
FK `recurring_transactions.loan_account_id → accounts(id, ledger_id)` (mig 168),
mirroring `source_account_id` (ADR-0047). Direction chosen deliberately: the
reverse (`accounts.payment_reminder_id → recurring_transactions`) would add a
cycle to the snapshot-restore insert order (accounts are inserted before
recurring_transactions, ADR-0037), forcing a `DEFERRABLE` FK — this direction
needs none. A **partial unique index** (`loan_account_id WHERE NOT NULL`) enforces
one managed reminder per loan. The migration backfills the existing inferred
links (`is_loan_reminder` series whose template posts to a loan account).

### D2 — Set up + surface it from the loan account editor

The loan account editor gains a "Scheduled payment" section (edit mode only — a
loan must exist first):
- **Linked** → shows the cadence + next due + a "Managed" badge; amounts are
  computed from the terms, and the reminder is managed/deleted on the Reminders
  page.
- **None** → "Set up scheduled payment": pick the paying bank account + a start
  date. `POST /accounts/{accountId}/payment-reminder` builds the loan-shape series
  (`is_loan_reminder`, template legs on loan/interest/escrow with **placeholder**
  amounts — the real split is derived live) with the cadence derived from the
  loan's payments-per-year. No amounts are entered. Read side:
  `AccountDetail.managedReminder`.

### D3 — The split reads the canonical balance

`ComputeLoanSplitsAsync` derives "owed" from `account_current_balances` (mig 133) —
the register's own `balance_after` — instead of re-summing raw legs, so it honors
merges + overrides (ADR-0034: running balances come from the canonical walk, never
a parallel re-sum). The amortized payment also rounds **up** to the cent (the
servicer convention, so a computed payment matches the real statement). Both
shipped in the loan-payment fix that immediately precedes this.

## Consequences

- Deleting the reminder (via the Reminders UI) clears the link automatically (the
  FK lives on the deleted row), so the editor reflects "not set up" again — no
  separate unlink path in v1.
- "Managed" is UI-explicit but behaviorally identical to the imported loan
  reminders; the importer's `is_loan_reminder` series are backfilled into the link.
- Cadences beyond monthly / weekly / biweekly / quarterly fall back to monthly;
  refine when a real loan needs a finer schedule.
