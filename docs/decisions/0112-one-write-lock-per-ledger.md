# 0112 — One write lock per ledger: writes that can recompute a ledger take turns

* Status: Accepted (amends 0084, 0097, 0102, 0108); D3 amended 2026-11-13 by
  [ADR-0114](0114-benchmarks-and-currency-conversion.md) (a typed budget target and a reporting-currency change take the lock
  first); D1 amended 2026-11-18 by [ADR-0072](0072-recover-misfiled-transactions.md) D2
  (the single-row restore is removed; a restore is the bulk unhide, which takes the lock
  as the other bulk writes do; settled by Claude, 2026-11-18)
* Date: 2026-11-05
* Related: [0032](0032-triggers-as-last-resort.md) (the recompute runs at the call sites and
  in interceptors, not in triggers),
  [0034](0034-header-walk-running-balance.md) (stored running balances),
  [0037](0037-snapshots-and-backups.md) (snapshot capture and restore),
  [0064](0064-fifo-cost-basis.md) (holdings, lots and realized gains),
  [0083](0083-multi-user-management-invites.md) (several people write to one ledger).

## Context

`fn_recompute_balances_for_account` deletes an account's stored running balances from an
anchor on and inserts them again; `recompute_holdings_cost_basis` does the same for a
position's realized gains, and updates its lots and holding. Every save that changes lines
runs them in its transaction, as do the bulk writers, the edits that recompute explicitly
and the consistency repairs. Two runs over one account or position at once collide unless
the later waits for the earlier to commit: the later insert meets the rows the earlier
committed (a unique violation, so the save fails and nothing is saved), or a balances run
reads past the earlier one's uncommitted transaction and stores a running balance that
leaves it out.

A lock taken when the run starts is too late. A writer row-locks and writes before it
recomputes: a delete's cascade to the stored balances, a merge's `FOR UPDATE`, the first
part of an edit saved in two parts. It would then wait for the run's lock while holding
rows the run waits on, and Postgres cancels one of the two. Taking every account and
position a writer will recompute up front does not work either, because on several paths
that set is not known before the first statement: a security merge reads it under its row
locks, a save's touched lines are read after the save, and a position whose first trade is
in flight has no holding row yet. Every path knows its ledger before its first statement.

## Decision

### D1 — One lock per ledger, taken first

Every transaction that writes to a ledger and can recompute its balances or holdings takes
the ledger write lock as its first statement: `fn_ledger_write_lock`, a transaction-scoped
advisory lock on `coffer.ledger-write:<ledger>` (migration 275), taken through
`BeginLedgerWriteAsync` (`LedgerWriteLock`), which opens a READ COMMITTED transaction when
none is open. A transaction waiting for it holds nothing in the ledger, and two that hold it
are never in the ledger at once, so no wait between two of them closes a cycle, however many
steps either recomputes in.

- A writer whose save would otherwise get its transaction from `LegNotesRecomputeInterceptor`
  opens its own through the helper: a single delete, the investment delete, and the ingest
  saves that recompute (the provider fetch stays outside it). The ingest's
  `last_simplefin_sync_at` update runs inside the final save's locked transaction.
  *(Amended 2026-11-18, [ADR-0072](0072-recover-misfiled-transactions.md) D2.)* The
  single-row restore is removed, so it is no longer among these writers: a restore is
  the bulk unhide, which takes the lock as the other bulk writes do (settled by Claude,
  2026-11-18).
- The trade-commission flip is one transaction: the flag and the brokerage's holdings
  recompute commit together.
- Every consistency repair takes it. The balances, holdings and realized-gains repairs take
  it one statement at a time, through the two recompute functions (D2), so a save waits for
  at most the one account or position in flight, not the whole repair.
- Snapshot restore, whose transaction is REPEATABLE READ, holds the session form
  (`fn_ledger_write_session_lock`) from before that transaction begins until after it ends,
  for the reason migration 247 gives for the snapshot-create lock: a lock taken inside the
  transaction would come after its snapshot.

### D2 — The database refuses a writer that misses it

The two recompute functions take the lock themselves, before they read anything they
write from. In a transaction that holds it, that does nothing; as a statement of its own,
it takes the lock for that statement; in a transaction that has written or row-locked
without it, it is refused (SQLSTATE 25000). A writer that misses the lock therefore fails
rather than races, and every test of a recomputing path exercises the refusal.
`fn_ledger_write_lock` also refuses any isolation level but READ COMMITTED (the writer must
read what the previous holder committed) and a session that holds another advisory lock
other than the same ledger's snapshot-create lock (25000), and a null ledger or a wait that
is not positive (22023).

Two transactions are exempt. The one that created the ledger row may take the lock after
writing: the Moneydance import fills a ledger no other session can see. And on a
migration's session, which `MigrationRunner` marks with `coffer.migration_session = on`,
the function does nothing, because a data migration writes before it recomputes and nothing
runs beside it. Nothing else sets that setting, and nothing but migration 275 names the
transaction-local marker the function keeps of the ledgers it holds,
`coffer.ledger_write_held`, which would skip the lock the same way
(`LedgerWriteLockCatalogTests`).

### D3 — One order

The ledger write lock comes first: then the reminder slot lock (migration 218), migration
274's step 0, migration 264's row and foreign-key locks, migration 274's steps 1 to 3, and
the stored-notes recompute's header locks. The snapshot-create lock (migration 247), a
session lock, comes before it: `snapshot_ledger_lock` refuses a session that holds the same
ledger's write lock. A cycle made only of row locks, with a transaction that does not take
the ledger write lock, stays under the orders and limits migrations 264 and 274 state.

Some writers that recompute neither balances nor holdings take the ledger write lock first
too, because they lock or write a row that a transaction holding it goes on to need while
it holds a row they need next; with the lock first they take turns with it instead:

- a tag merge, delete or clean-up locks the tag (`fn_lock_tags`), then the headers its
  stored-notes recompute locks, while a save that holds a header links the tag (its
  foreign key takes `KEY SHARE` on the tag);
- a budget fill (copy last month, fill from average) writes the month's targets, while a
  category merge locks its source and moves the source's targets onto the destination's;
- a feed connection's delete locks the connection, then its cascade updates the accounts
  it feeds, while a sync's final save holds those accounts' watermark rows and inserts a
  row whose foreign key names the connection;
- a ledger's delete wipes every row of the ledger, which any write in flight may hold.

*(Amended 2026-11-13, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D7 and D12.)* Two more writers take it first, for an
ordering reason rather than a cycle: a typed budget target (`BudgetTargetsRepository.SetAsync`)
reads the ledger's reporting currency and stamps it on the target, and a reporting-currency
change (`PATCH /api/ledgers/{id}`, `LedgersRepository.SetReportingCurrencyAsync`) writes
that currency. With both under the lock, a target written beside the change either commits
before it, in the old code, or waits for it and is stamped with the new one, so no target
commits in a code the change had already replaced. The budget fill above already takes the
lock, so what it stamps is current too.

### D4 — A bounded wait, and a refusal that can be retried

The lock waits at most half the context's command timeout: 15 seconds on the API's default
of 30, so the wait is refused before the command timeout cancels the statement. Past it the
database raises SQLSTATE `LW001`, and the API answers 422 `ledger-write-busy`: "Another
change to this ledger is still being saved. Nothing was changed; try again in a moment."
Nothing was written, and the same request succeeds once the other write has committed. A
consistency repair takes the lock one piece at a time (D1) and keeps the pieces it
committed before the refusal, so its 422 says that instead: "Another change to this ledger
is still being saved, so the repair stopped. Anything it had already repaired stays
repaired; repair again to finish." A background writer refused this way tries again on its
next run.

### D5 — Writes to one ledger take turns

Every write in D1, and each writer D3 adds, waits for any other in flight on the same
ledger, even when they share no account: an interactive save waits for a bulk delete or an
undo import, a file import's or feed sync's recomputing save, a reminder auto-post, a
merge, a restore, a tag merge, a budget fill, or one statement of a repair. Reads, snapshot
creates and writes to other ledgers do not wait.

This supersedes two recorded choices for writes of one ledger:

- **Reminder slots fire one at a time.** ADR-0097 keyed the fire/skip lock on the slot, not
  the series row, so that two dates of one series could fire at once (migration 218). Every
  fire now takes the ledger write lock before the slot lock, so fires of one ledger take
  turns. The slot lock still serialises a fire and a skip of one occurrence.
- **Trade-price re-derivations of one ledger wait for each other.** In ADR-0084 D2's lock
  order (migration 274) a re-derivation locks its accounts `FOR SHARE`, which does not
  conflict with itself, so that step lets re-derivations, and a re-derivation and the
  holdings-currency repair, run at once. Every path in that order that can recompute
  balances or holdings, and every repair, takes the ledger write lock first, as do a
  currency edit and a re-derivation that opens its own transaction, so within one ledger
  those take turns before that step. A re-derivation run in a caller's transaction that recomputes neither
  takes none, and steps 1 to 3 of that order keep it from a cycle.

## Consequences

- A repository whose write can recompute balances or holdings opens its own transaction,
  through the helper (engineering-standards §3.5, rule 1). A save that recomputes runs in
  its writer's transaction, opened with the lock; `LegNotesRecomputeInterceptor`'s fallback
  transaction, opened in the save itself, serves only saves that recompute neither
  (engineering-standards §3.5, rule 4; ADR-0108 D6).
- Throughput within one ledger is one recomputing write at a time. A long write (an undo
  import, a large sync) makes a save behind it wait, and past the bound refuse with
  `ledger-write-busy` rather than fail with a timeout.
- Two ledgers whose keys hash alike take turns; nothing else follows from a collision.
- Guarded by `LedgerWriteLockTests`, which pin each refusal, the exemptions, the order
  against the snapshot-create lock, the bounded wait and both forms against restore, and by
  the refusal in the recompute functions, which every existing test of a recomputing path
  runs through. The writers D3 adds recompute nothing, so the refusal cannot catch one that
  misses the lock: `LedgerWriteLockBankPathsTests` runs each while another transaction
  holds it and asks that the lock is the only statement it sent before it waited.

## Alternatives considered

**A lock per account and per position, taken by the recompute functions.** Closes two runs
over the same rows. Rejected: a writer that row-locks or writes before it recomputes waits
for that lock while holding what the run that has it waits on, which deadlocks.

**A lock per account and per position, the whole set taken before the first row lock.**
Keeps writes to different accounts concurrent. Rejected: where the set is read after the
first statement (a security merge, a save's touched lines, the commission flip's positions,
an in-kind conversion's brokerage, a pull's promoted counterparties) each path needs a
refusal of a late key and a retry of the whole transaction, so "cannot deadlock" becomes
"refused and retried", at about three times the change.

**The repair alone retries a statement Postgres cancels as a deadlock victim.** Smallest.
Rejected: which transaction Postgres cancels cannot be relied on, so a writer would still
fail with nothing saved, and it leaves the deadlocks between two saves open.
