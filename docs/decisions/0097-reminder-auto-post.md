# 0097 — Reminder auto-post is a scheduled job, and one occurrence posts once

* Status: Accepted; amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (a managed loan series whose paying
  account or real escrow account is in another currency than the loan's is set aside
  before the per-run cap, with the exclusion reason `loan-across-currencies`, counted as
  `managed_loan_across_currencies` and named; a fire refused because the estimate is
  unavailable, with no history or no rate, is counted as `estimate_unavailable` and stays
  due, not resolved by hand); amended 2026-11-16 by ADR-0114 D15 (a series whose stored
  figure does not fit its currency is set aside before the per-run cap, with the
  exclusion reason `template-amount-does-not-fit`, counted as
  `template_amount_does_not_fit`, and refused at fire by the same predicate; named since
  2026-11-18); amended 2026-11-18 (a ledger whose reminders ask to be auto-posted and
  that has no job is a consistency finding, `reminder_auto_post`, whose Repair turns the
  job on)
* Date: 2026-09-01

## Context

`RecurrenceBuilder.tsx` renders an "Auto-post / N days before" control. The value
validates (`BusinessError.ReminderAutoCommitNegative`), persists through
`RemindersRepository`, and round-trips on edit. **Nothing fires it.** `src/Api/Scheduling`
carries handlers for backups, feed sync, quotes and snapshots and none for reminders,
and nothing outside DTO plumbing reads `AutoCommitDaysBefore`. A user who sets it has
been told their rent posts itself; it does not, the failure is silent, and the missing
transaction looks like their own oversight.

The alternative is removing the control until a worker exists; this ADR builds the worker.

Two properties dominate the design, because this writes financial transactions on a
timer with nobody watching:

* An occurrence must post **at most once**, across restarts, retries and concurrent
  callers.
* An occurrence the user **skipped** must not post, including when the skip lands
  concurrently with the timer.

Neither holds for a read-then-insert. A `FireAsync` that decides both questions with a
`SELECT` and then inserts — its idempotency read and its skip read both running *before*
it opens its transaction, so not even repeatable-read against the write they guard — has
a race window. With a human clicking Fire the window is small; a timer makes the second
caller a machine.

## Decision

**Auto-post is a first-class `scheduled_jobs` job type**, not a monitor riding the
scheduler tick.

`ConsistencyMonitor` may ride the tick because it only OBSERVES. This WRITES. A job type
is what buys failure counting, auto-disable after five consecutive failures, the monitor
binding, per-ledger enable, and — since 0.71.0 — a recorded `disabled_reason`. A writer
that can silently stop is the outage class the notification work exists for. The cost is
widening `ck_scheduled_jobs_type`, and it is accepted.

**One fire per occurrence is enforced by the database** — a partial unique index on
`txn_headers (recurring_transaction_id, occurrence_date)` where the stamp is present and
the row is not a template (migration 218). `FireAsync` converts a `23505` on that index
into the same answer the sequential path returns, so a race is idempotent rather than a
500.

**Fire and skip serialise on a shared advisory lock**, keyed on the (series, occurrence)
SLOT, taken inside the caller's transaction by every fire path and by skip. They write to
*different tables* — fire inserts a `txn_header`, skip inserts a
`recurring_occurrence_exception`, each after reading the other's — so no constraint can
exclude the pair. Interleaved, both pass their checks and the slot ends up fired AND
skipped. Only a lock both paths take prevents that.

*Amended 2026-11-05 by [ADR-0112](0112-one-write-lock-per-ledger.md): fires of one ledger
take turns.* A fire recomputes balances, so every fire path takes its ledger's write lock
as its transaction's first statement, before the slot lock (ADR-0112 D1, D3). Two fires of
one ledger, two dates of one series among them, therefore run one after the other. The slot
lock still serialises a fire and a skip of one occurrence; skip, which recomputes nothing,
takes the slot lock alone.

**`AutoCommitDaysBefore` is capped, and the early balance effect is accepted.** The
transaction is written N days early but DATED at its due date, so the ledger's balance
and net worth drop N days before the money leaves. Validation today is only `N >= 0`,
which lets N exceed the recurrence interval and open an unbounded pipeline of
future-dated real transactions. A cap bounds it. A pending / not-yet-effective concept
is the correct answer to the underlying problem and is a feature in its own right; it is
not being smuggled in under auto-post.

*Amended 2026-10-21: the early balance effect is gone. Since mig 259 a current balance
stops where the register's Scheduled begins ([ADR-0056](0056-ledger-overview-dashboard.md),
2026-10-21 amendment), so a transaction auto-posted N days early, dated at its due date,
does not move the sidebar, the dashboard, net worth or the MCP balances until the
viewer's date reaches it, exactly when the register stops showing it as Scheduled. The
register still lists it, with the running balance it will leave. The cap stands; it
bounds how many future-dated real transactions a series can open, which 259 does not
change.*

*Amended 2026-10-21 (0.126.1): the job posts a loan payment with the split priced as of
the payment's own date, never today.* A managed loan's principal/interest split is priced
on the loan's balance as of the occurrence's `posted_at` (every row before it in the
register's order, nothing after it; [ADR-0078](0078-managed-loan-payment-reminder.md),
2026-10-21 amendment), exactly as a hand fire and the occurrence dialog price it. The
run's time, the server's UTC date and the ledger's zone play no part in it: the run's
ledger-local date (`JobRunClock.LocalToday()`) decides only which occurrences are due,
and the job reads no other "today". A payment posted early by an earlier run, being
dated before the next occurrence, counts against it.

*Amended 2026-10-23: a reminder whose template has no lines is never posted, and is
named.* Such a template names no account to post from (migration 263), so the
verbatim-clone fire refuses it before writing anything. The job excludes such a series before
the per-run cap, as it excludes investment-shape series, so it cannot fail every run or
take a postable occurrence's slot. It is counted in the `reminder-backlog.deferred`
warning (`template_has_no_lines`), and the summary names up to three such reminders,
with no dates or amounts, because only the user can fix one: give it its lines, or
delete it.

*Amended 2026-10-24: the verbatim clone is the job's write alone.* The plain
`POST …/reminders/{id}/fire` route is removed ([ADR-0049](0049-reminders-spa.md),
2026-10-24 amendment): the API serves only the UI, and a person posts an occurrence
through `/fire/bank` or `/fire/investment`. `FireAsync` therefore has one caller, and
carries the job's rules itself rather than taking them as arguments: it never marks
earlier occurrences skipped, and it refuses an investment template before writing
anything, as the job already excluded one.

*Amended 2026-10-24: a reminder whose template uses an inactive account is never
posted, and is named.* A new transaction never lands on a retired account; every
by-hand post refuses one with `account-inactive`. The job sets a bank series with a line
on an inactive account aside before the per-run cap, exactly as it does a template with
no lines: not posted, not a failure, and not taking a postable occurrence's slot. It is
counted in the `reminder-backlog.deferred` warning (`template_uses_inactive_account`),
and the summary names up to three such reminders (payee, else "Untitled reminder"), with
no dates or amounts, and tells the user to reactivate the account or edit the reminder.
An investment series is excluded as investment first, whatever its accounts. The clone
fire also refuses such a template itself, inside the slot's transaction, so an account
retired between the job's scan and its fire is still not posted to.

*Amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D13: two refusals at fire time are new, and an
unavailable estimate is counted.*

- **A managed loan series across currencies is set aside.** A series whose paying
  account or real escrow account is in another currency than the loan's predates the
  setup guard and is refused at fire time ([ADR-0078](0078-managed-loan-payment-reminder.md) D2), so it can never post. The job
  sets it aside before the per-run cap, as it does a template that uses an inactive
  account: not posted, not a failure, and not taking a postable occurrence's slot. It is
  excluded with the reason `loan-across-currencies` and counted in the
  `reminder-backlog.deferred` warning under its own key,
  `managed_loan_across_currencies`, and the summary names such reminders as it names
  those that use an inactive account. Like the other reasons it is a series property,
  so it joins `AutoPostExclusion` and every reader that says whether an occurrence posts
  by itself agrees with the job.
- **An unavailable estimate is counted, not resolved by hand.** An estimating transfer
  between currencies with no rate for its due date (`RateDay.Of`, ADR-0114 D7, D9; the
  rate is derived from both accounts' stored rates against the reporting currency, and
  is missing when either is, which the reason names as that stored leg and the day,
  ADR-0114 D9, D13) has no estimate, as a series with no history
  has none. Either way the fire is refused with
  `EstimateUnavailableForFire` and nothing is written. The job counts that refusal in the
  `reminder-backlog.deferred` warning under its own key, `estimate_unavailable`, and the
  occurrence stays due for the next run. It is not a failure, so it never walks the job
  toward the auto-disable. The existing no-history case changes with it: the job counts
  every fire that does not post as resolved by hand, a person having acted on the slot
  between the scan and the fire (`ReminderAutoPostJobHandler.cs:177-187`), so an
  unavailable estimate is logged and counted nowhere today. A refusal by
  `EstimateUnavailableForFire` leaves that count and takes the new key. With a rate, the
  job posts the source side's estimate and the other side at that rate.

*Amended 2026-11-16 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15: a
series whose stored figure does not fit its currency is set aside, and the reason codes
are named* (settled by Claude, 2026-11-16).

- **A figure that does not fit is set aside.** A series whose stored figure (a template
  line, a loan split or its override, or the estimate in its place) has more places than
  its currency allows is set aside before the per-run cap, as an inactive-account one
  is. Its exclusion reason is `template-amount-does-not-fit`, and it is counted in the
  `reminder-backlog.deferred` warning under `template_amount_does_not_fit`. The summary
  names it, as it names a series that uses an inactive account, appending " {count}
  reminder(s) cannot be posted because a figure has more decimal places than its
  currency uses ("{name}"; ...). Edit each reminder, or its loan's terms, so every
  figure fits its currency." after the other named reasons (the text decided by the
  owner, 2026-11-18, ADR-0114 D15; its place last settled by Claude), and the consistency
  check's advisory lists its line. The fire refuses
  it too, judged by the same predicate, so the scan and the fire agree; a fire refused
  this way, for a series changed between the scan and the fire, is counted under the
  same key, is not a failure, and stays due until the reminder or the loan terms are
  edited.
- **The reason codes, in the order they are judged.** Each series takes the first that
  applies: `no-lines`, `investment`, `inactive-account`, `loan-across-currencies` (the
  managed loan series across currencies above) and `template-amount-does-not-fit`. A
  loan across currencies is refused before its split is priced, so it is judged before
  a figure that does not fit.

*Amended 2026-11-18: a ledger whose reminders ask to be auto-posted and that has no job is
a consistency finding, and its Repair turns the job on.* A reminder asks when its
`auto_commit_days_before` is set, whatever its state: an inactive one asks too, since
reactivating a reminder turns no job on. The job is created, enabled at the default hour
and never changed once it exists, by a reminder saved with Auto-post and by a Moneydance
import that brings one set to auto-commit; the import and the check read one definition
of "asks" (`AutoPostRequests`). A ledger imported, or a reminder saved, by a version that
did not create the job has a reminder shown as auto-posting and nothing posting it. The
consistency check's `reminder_auto_post` projection reports that ledger, one finding
saying how many reminders it leaves unposted; its Repair creates the job as those writes
do, configured by whoever repairs, under the ledger's write lock
([ADR-0112](0112-one-write-lock-per-ledger.md)). A job row that exists is never a finding,
whatever its state (the job has no off switch, and the scheduler never disables it), and
the Repair never changes one.

## Consequences

The scheduler gains a job type that writes money, so its failure-counting and
auto-disable machinery becomes load-bearing rather than defensive.

Every fire path now opens its transaction before its checks and takes a lock. That is a
small latency cost on the manual path, paid so the two paths cannot diverge — a
correctness rule enforced in one place beats the same rule re-derived by each caller.

An upgrade FAILS LOUDLY, naming the series and date, if an install already carries a
duplicate `(series, occurrence)` pair. That cannot happen on an install that never
raced, which is expected to be all of them; it is there because "expected to be" is not
"verified", and resolving a duplicated money row by picking an arbitrary winner is not a
migration's decision to make.

The balance effect stays visible: a user with a large N sees the money leave early. This
is documented rather than fixed, and it is the strongest argument for the pending concept
later. *Amended 2026-10-21: no longer true of the current balances, which stop where the
register's Scheduled begins since mig 259 (see the Decision's amendment). It remains
true of the register, which lists the early-posted row, as Scheduled, with the balance it
will leave.*

`reminder_occurrence_lock` is a Postgres function because `src/Api` may not write raw
SQL. It is transaction-scoped, so a pooled connection cannot be returned still holding
it.

## Alternatives considered

**Ride the scheduler tick like `ConsistencyMonitor`.** Cheaper — no migration, no CHECK
widen, and `MonitorScopeTests` explicitly sanctions a monitor that is not a job. Rejected:
monitors observe, and nothing about a monitor gives failure counting, auto-disable or a
per-ledger switch. A money writer with none of those is the thing that fails silently.

**`SELECT ... FOR UPDATE` on the series row instead of an advisory lock.** Simpler to
read, and it serialises every occurrence of a series against every other — including a
catch-up run posting a backlog, which is exactly where throughput matters. The advisory
key is the slot, so two dates in one series still proceed in parallel. *Superseded
2026-11-05 by [ADR-0112](0112-one-write-lock-per-ledger.md) for fires: every fire takes
its ledger's write lock first, so fires of one ledger take turns whatever their slot. The
slot remains the key, for a fire and a skip of one occurrence.*

**Application-level idempotency only** (the existing read-then-write, tightened). Rejected:
at READ COMMITTED two callers both read nothing and both insert, and no amount of care in
C# closes that without a constraint behind it.

**Cap `AutoCommitDaysBefore` below the recurrence interval** rather than at a fixed
ceiling. Structurally stronger — at most one occurrence per series can ever be pending —
but it rejects configurations a user may already have saved, and the interval is not
always a simple number for an arbitrary RRULE.

**Remove the auto-post control until the worker exists.** The honest short-term fix for
a UI that lies. Rejected: building the worker closes the gap instead of hiding it.
