# 0097 — Reminder auto-post is a scheduled job, and one occurrence posts once

* Status: Accepted
* Date: 2026-09-01

## Context

`RecurrenceBuilder.tsx` renders an "Auto-post / N days before" control. The value
validates (`BusinessError.ReminderAutoCommitNegative`), persists through
`RemindersRepository`, and round-trips on edit. **Nothing fires it.** `src/Api/Scheduling`
carries handlers for backups, feed sync, quotes and snapshots and none for reminders,
and nothing outside DTO plumbing reads `AutoCommitDaysBefore`. A user who sets it has
been told their rent posts itself; it does not, the failure is silent, and the missing
transaction looks like their own oversight.

The alternative was removing the control until a worker existed. Ranking Reminders first
settles it the other way: build the worker.

Two properties dominate the design, because this writes financial transactions on a
timer with nobody watching:

* An occurrence must post **at most once**, across restarts, retries and concurrent
  callers.
* An occurrence the user **skipped** must not post, including when the skip lands
  concurrently with the timer.

Neither held before this work. `FireAsync` decided both questions with a `SELECT` and
then inserted — and its idempotency read and its skip read both ran *before* it opened
its transaction, so they were not even repeatable-read against the write they guarded.
With a human clicking Fire the window is small and nobody has hit it. A timer makes the
second caller a machine.

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

**`AutoCommitDaysBefore` is capped, and the early balance effect is accepted.** The
transaction is written N days early but DATED at its due date, so the ledger's balance
and net worth drop N days before the money leaves. Validation today is only `N >= 0`,
which lets N exceed the recurrence interval and open an unbounded pipeline of
future-dated real transactions. A cap bounds it. A pending / not-yet-effective concept
is the correct answer to the underlying problem and is a feature in its own right; it is
not being smuggled in under auto-post.

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
later.

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
key is the slot, so two dates in one series still proceed in parallel.

**Application-level idempotency only** (the existing read-then-write, tightened). Rejected:
at READ COMMITTED two callers both read nothing and both insert, and no amount of care in
C# closes that without a constraint behind it.

**Cap `AutoCommitDaysBefore` below the recurrence interval** rather than at a fixed
ceiling. Structurally stronger — at most one occurrence per series can ever be pending —
but it rejects configurations a user may already have saved, and the interval is not
always a simple number for an arbitrary RRULE.

**Remove the auto-post control until the worker exists.** The honest short-term fix for
a UI that lies, and the right call if Reminders were not being worked on. It is, so the
worker gets built instead.
