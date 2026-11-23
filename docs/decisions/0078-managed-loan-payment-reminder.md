# 0078 — Managed loan-payment reminder

Status: Accepted; D2 amended 2026-11-14 and D3 2026-11-16 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (D2: setup and
fire are refused when the paying account or a real escrow account is in another currency
than the loan's; D3: the payment rounds up to one minor unit of the loan's currency, and
the split rounds at its places)
Date: 2026-07-16
Relates: [ADR-0047](0047-reminders-recurring-transactions.md), [ADR-0050](0050-account-editor-and-loan-amortization.md), [ADR-0034](0034-header-walk-running-balance.md), [ADR-0037](0037-snapshots-and-backups.md)

## Context

A loan's scheduled auto-payment is a recurring reminder (ADR-0047) whose
principal/interest/escrow split is computed live from `loan_terms` + the loan
account's current balance (ADR-0050). That arrangement has two gaps:

1. The reminder↔loan relationship exists only by **inference** (a series whose
   template posts to a loan account) and is set exclusively by the Moneydance
   import — invisible and unmanageable in the UI.
2. The split's "balance owed" is **re-summed from raw `txn_legs`**, which
   diverges from the register's canonical balance (it double-counts
   `is_merged_into` duplicates and ignores leg overrides), throwing off the
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

*(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D13.)* Setup is refused when the paying account's
currency differs from the loan's, and when a real escrow account is in another currency
than the loan's, because interest and escrow are computed in the loan's currency. An
escrow that is a category mirrors as usual. A series created before this guard is
re-checked at fire time and refused there. Such payments are entered as ordinary
transfers with both amounts.

### D3 — The split reads the canonical balance

`ComputeLoanSplitsAsync` derives "owed" from `account_current_balances` (mig 133) —
the register's own `balance_after` — instead of re-summing raw legs, so it honors
merges + overrides (ADR-0034: running balances come from the canonical walk, never
a parallel re-sum). The amortized payment also rounds **up** to one minor unit of the
loan's currency (the servicer convention, so a computed payment matches the servicer's
statement). *(Amended 2026-11-16, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15: it rounded up to the cent, which
is one minor unit only for a 2-place currency. The split's interest and principal are
rounded once at the loan's currency's places, half away from zero, as every figure the
API computes is (settled by Claude, 2026-11-16).)*

*Amended 2026-10-21 (0.126.1): "owed" is the loan's balance as of the payment's own
date, never today.* The rule: the split is priced on the balance as of
the payment's date, "every row dated before that payment, and nothing dated after
it"; it never uses "today" in any time zone, and when the job runs, or who fires the
occurrence, is irrelevant. The precise cut is the register's own order,
`(posted_at, seq)`: "owed" is the running balance just before the payment. A row with
an earlier `posted_at` counts; a row on the same instant counts too, because the
payment, posted after it, takes the next `seq` and so follows it; a row after the
payment's `posted_at` (a later date, or a later time on its day) does not. The read is
`account_balance_as_of_instants` at the payment's `posted_at` (`posted_at <= as_of`,
ties included), the as-of balance the net-worth history uses, so it honors merges and
overrides like every running balance (D3). The payment's `posted_at` is the occurrence
date at midnight UTC, the same instant the fire stamps on the row.
- **Decided: an adjusted fire is priced at the date it posts, not the occurrence's.**
  `/fire/bank` takes an optional posted date; when it differs from the occurrence date,
  the split is priced as of the posted date, the `posted_at` the row is stamped with. The
  rule's test is "the date of the transaction being posted", and the running balance
  the posted row then sits on is the one it was priced against. The preview prices the
  occurrence date, and agrees with the fire because the occurrence dialog posts a
  managed loan payment on that date; an API caller that moves the date gets the split
  for the date it chose.
- **Every path prices the same way:** the auto-post job, firing by hand (`/fire/bank`;
  the plain `/fire` route is removed, ADR-0049 2026-10-24 amendment), and the occurrence
  dialog's preview, which reads the detail for that
  occurrence (`GET …/reminders/{id}?occurrenceDate=`; without one, the series' next due
  occurrence). None takes a date from the caller or the clock.
- **A payment fired ahead and dated before this one counts**, because its date is
  earlier: firing several occurrences ahead prices each net of the ones before it. A
  payment dated after this one never counts against it, whenever it was entered.
- **The full payment shown on the agenda, the manage list and MCP
  `list_upcoming_reminders`** is principal + interest + escrow, which is the periodic
  payment plus escrow whatever the balance, so it reads no balance at all.
- **Not a current balance.** It does not follow the Scheduled boundary of
  [ADR-0056](0056-ledger-overview-dashboard.md) (2026-10-21 amendment): that boundary
  answers "what is the balance now", and this asks what was owed when the payment is
  made. Rejected: pricing on the current balance at the caller's date (it made a
  payment fired ahead depend on the day it was fired, and the job's split depend on the
  server's UTC date) and on the balance after the register's last row (it counted
  payments dated after this one).

Pinned by `RemindersLoanTests`: a payment fired ahead and dated before the occurrence
is counted; a later-dated payment is not, and a same-day row entered first is, with
"owed" equal to the register's running balance just before the posted payment; the
job splits alike on the evening before west of UTC and on the due date in UTC, and
the preview and a hand fire agree with it.

## Consequences

- Deleting the reminder (via the Reminders UI) clears the link automatically (the
  FK lives on the deleted row), so the editor reflects "not set up" again — no
  separate unlink path in v1.
- "Managed" is UI-explicit but behaviorally identical to the imported loan
  reminders; the importer's `is_loan_reminder` series are backfilled into the link.
- Cadences beyond monthly / weekly / biweekly / quarterly fall back to monthly;
  refine when a real loan needs a finer schedule.
