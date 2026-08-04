# 0034 — Header-walk running balance + canonical ordering

* Status: Accepted
* Date: 2026-05-29
* Supersedes (partially): [ADR-0004](0004-balance-after-trigger.md) (trigger now walks headers, not transactions/legs), [ADR-0028](0028-investment-register-surface.md) §"MAX(posting_index) picker" (no longer load-bearing — gone)
* Related: [ADR-0019](0019-symmetric-postings.md), [ADR-0022](0022-txn-headers-and-legs.md), [ADR-0032](0032-triggers-as-last-resort.md)

## Context

ADR-0022 split `transactions` into `txn_headers` + `txn_legs`. The running-balance trigger from ADR-0004 was mechanically rewritten to walk legs ordered by `(h.posted_at, l.id)` (mig 023). The semantic shift — from "one row per logical event" to "N rows per event" — was not reflected in the running-sum logic. **The bug has been latent since ADR-0022 landed.**

It only surfaced now because Slice A4's BuyXfr fan-out is the first event type in production data that puts **multiple cash legs on the same account inside one header**. Bank single-postings hide the bug. Two visible symptoms:

1. **Intermediate balance jumps.** BuyXfr writes two cash legs that net to $0 (one for the buy debit, one for the transfer-in credit). With leg-walk, the running balance jumps to +$N then back; the register cell for one leg displays a value that doesn't represent any real cash state.
2. **Non-deterministic ordering on same-timestamp transactions.** Sort key `(posted_at, l.id)` falls back to a random UUID when timestamps collide. Worse, two consumers can pick *different* UUID columns as the tiebreaker — the trigger sorting by `h.id` while `register_entry_keys` falls back to `l.id` for single-posting events — producing balances that cascade correctly with one application order but display in a *different* order, looking visibly scrambled.

The read side already does the right thing: `register_entry_keys` (mig 029) groups by header and orders by `(posted_at DESC, created_at DESC, entry_key DESC)`. Read order and write order disagree.

The same shape can appear in **any future split transaction**, investment or not: a refund applied to a purchase, a cash dividend with a fee on a sweep account, a bill paid across multiple categories from one checking account. The fix must live at the structural level, not in an investment-specific branch.

## Decision

Three changes, atomic:

### 1. The trigger walks headers, not legs

Per `(account, header)`, aggregate the net cash effect once, then step the running total once per header:

```sql
WITH header_net AS (
    SELECT h.id, h.posted_at, h.seq,
           SUM(l.amount) AS net_amount
      FROM txn_headers h
      JOIN txn_legs l ON l.header_id = h.id
     WHERE l.account_id = $account
       AND h.is_merged_into IS NULL
       AND h.posted_at >= $window_start
     GROUP BY h.id, h.posted_at, h.seq
)
SELECT id, anchor + SUM(net_amount) OVER (
    ORDER BY posted_at, seq
    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
) AS balance_after
  FROM header_net;
```

BuyXfr now contributes a single $0-net step. Multi-leg same-account effects collapse to one entry in the running sum.

### 2. Canonical ordering: `(posted_at, seq)`

`seq` is a strictly-monotonic `BIGINT` column on `txn_headers` populated by a Postgres SEQUENCE (`txn_headers_seq`). It is **the** tiebreaker for every running-window calculation on transactions, on every code path.

**Why `seq` and not `(created_at, id)`:** the initial design used the triple `(posted_at, created_at, id)`. Real data exposed the gap — the importer batch-INSERTs all headers in one statement, so every header in the batch gets the *identical* `created_at` (`now()` is evaluated once per statement). Ordering then falls through to `id`, a random UUID. Worse, different consumers picked different UUID columns as the tiebreaker (the trigger sorted headers by `h.id`; `register_entry_keys` fell back to `l.id` for single-posting events). The visible symptom was a register where balance values cascaded correctly with *some* application order but the rows were displayed in a *different* random order — top-down looked scrambled.

`seq` removes the failure mode at the root:

* It's strictly monotonic by insertion. Within a batch, each row receives a distinct value from `nextval()`. No tiebreaker is ever needed beyond `seq` itself.
* It's globally unique (no per-ledger sequence is needed; we filter by ledger before sorting).
* It's immutable — mig 095 installs `trg_reject_txn_headers_seq_update`, mirroring the `created_at` lockdown (mig 093). Both locks remain: `seq` is load-bearing, `created_at` is audit data.

The pair `(posted_at, seq)` is then unambiguous, deterministic, and shared by every consumer: the recompute trigger, `resolved_transactions`, `register_entry_keys`, `HoldingsRepository`, the register pagination cursor. Future columns that need a running-window calculation MUST use this pair. New ordering schemes for transactions are rejected at review.

### 3. Storage moves to `txn_header_account_balances(header_id, account_id, ledger_id, balance_after)`

Per-`(header, account)` storage matches the natural grain of the accounting model: a header touches N accounts, each with its own net cash effect and its own running balance.

* Cash legs become an implementation detail of how the effect is *recorded*, not the unit of *accounting*.
* The "pick the right leg" rule from ADR-0028 (MAX posting_index) **disappears entirely**. The view reads `(header, account) → balance`, no picker.
* RLS: denormalized `ledger_id` with composite FK to `txn_headers(id, ledger_id)`, matching the pattern from migration 049.

`txn_legs.balance_after` is dropped (mig 092).

## Consequences

**Positive**

* Multi-leg same-account headers produce a single, sensible balance per header.
* Recomputes are byte-identical across runs (deterministic ordering on immutable columns).
* Universally applies to any split transaction, not just investments.
* Read path is simplified — `JOIN txn_header_account_balances` instead of "find the right leg".
* ADR-0028's MAX-picker is gone; less surface area to maintain.

**Negative**

* Cross-table read for every register row (one extra JOIN). Mitigated by `PRIMARY KEY (header_id, account_id)` — index-only lookup.
* One-time backfill cost on migration apply. Acceptable; runs once at API restart.

**Cleanup folded into this slice**

* `txn_legs.balance_after` column removed.
* Importer's `ValidateCommand` (and its `GetBalanceSnapshotsAsync` / `AccountBalanceSnapshot` helpers) deleted. The legacy `transactions` table it queried was dropped in mig 025 (ADR-0022 Phase 2); the command has been broken since then. The CLI registration in `Program.cs` is removed in the same PR.

## Alternatives considered

* **Keep `balance_after` on `txn_legs`, write same value to every cash leg of `(account, header)`.** Smaller migration. Rejected: the picker rule (ADR-0028 MAX posting_index) stays load-bearing, "leg is the unit of accounting" remains an incorrect mental model, and future running-window columns inherit the same trap.
* **Compute on read via window function.** Quadratic for cursor pagination over long histories; rejected for the same reasons as ADR-0004.
* **Move `balance_after` onto `txn_headers` as a JSONB blob keyed by account.** Ugly; defeats indexing.

## Implementation notes

* Migration 089: create `txn_header_account_balances` + RLS + grants + indexes.
* Migration 090: header-walk recompute function, trigger swap, one-shot backfill.
* Migration 091: rewrite `resolved_transactions` to source `balance_after` from the new table.
* Migration 092: drop `txn_legs.balance_after`.
* Migration 093: column-level immutability trigger on `txn_headers.created_at`.
* Migration 094: LEFT JOIN + COALESCE fix in the legs DELETE branch (cascade-from-header race, same fix as mig 026 for the old leg-walk family).
* Migration 095: add `txn_headers.seq` (SEQUENCE-backed BIGINT), backfill in `(created_at, id)` order, lock immutability, index for the register's `(posted_at DESC, seq DESC)` read pattern.
* Migration 096: swap the recompute trigger to `ORDER BY (posted_at, seq)` and re-backfill so existing balances align with the new ordering.
* Migration 098: add `txn_header_account_balances.net_amount` — per-(header, account) cash delta stored alongside the cumulative `balance_after`. The recompute trigger already aggregates it in the `header_net` CTE; this surfaces it for read consumers so the SPA's `groupAmount(legs)` becomes a column lookup instead of a sum loop.
* Migration 099: close the override-on-`posted_at` gap. `fn_recompute_balances_for_account` now anchors / windows / sorts on `COALESCE(o.posted_at, h.posted_at)`; new statement-level triggers on `txn_header_overrides` (INSERT/UPDATE/DELETE) recompute affected accounts anchored at `MIN(old, new, h)` posted_at.
* Migration 100: `resolved_transactions` projects `header_account_net_amount` alongside `balance_after`. `ResolvedTransactionDto` carries the new field; SPA's `groupAmount(legs)` reads `legs[0].headerAccountNetAmount` directly (falls back to leg-sum only during transient ingest states).
* Migration 101: close the last ADR-0004 §4 override caveat. `fn_recompute_balances_for_account` sums `COALESCE(lo.amount, l.amount)` so `txn_leg_overrides.amount` is first-class for the balance walk. Three new statement-level triggers on `txn_leg_overrides` (INSERT / UPDATE-of-amount / DELETE) recompute the affected leg's account at its header's effective `posted_at`. Every override layer (posted_at, amount, payee/memo/status/etc.) now either participates in the balance walk or is intentionally excluded.
* Migration 102: **drop the entire balance-trigger family** (the eleven triggers from mig 090 / 094 / 099 / 101, plus their handler functions). The recompute function (`fn_recompute_balances_for_account`) stays as the algorithm; a TVF wrapper (`recompute_balances_for_account`) exposes it for `HasDbFunction`-bound LINQ. Per ADR-0032's "triggers as last resort", the recompute moves to API call sites: an EF `SaveChangesInterceptor` (`BalanceRecomputeInterceptor`) scans `ChangeTracker` and invokes the recompute automatically for every API write that mutates legs / headers / overrides. Bulk operations that use `ExecuteUpdateAsync` / `ExecuteDeleteAsync` (which bypass the ChangeTracker) invoke `BalanceRecomputeService` explicitly; the Moneydance importer (Dapper, no EF) does the same. The mig-102 one-shot DO-block backfill is the LAST trigger-implicit recompute the schema will ever see — afterward the responsibility belongs to the API + importer.
* Migration 103: `is_hidden` joins `is_merged_into` as a recompute-time filter. `fn_recompute_balances_for_account`'s `header_net` CTE adds `AND COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE`, matching the resolved view's effective-hidden expression (mig 100). Before mig 103, soft-hiding a transaction (the bank + investment DELETE soft-hide branches, plus bulk-delete's soft-hide branch) removed it from the register but kept its amount in every downstream `balance_after` — the visible row was gone, the math wasn't. The `BalanceRecomputeInterceptor` now treats `IsHidden` modifications on either `TxnHeaderRow` or `TxnHeaderOverrideRow` as balance-affecting; `BulkTransactionsRepository.BulkDeleteAsync`'s soft-hide branch (which uses `ExecuteUpdateAsync` and bypasses the interceptor) captures affected `(account_id, posted_at)` pairs and invokes `BalanceRecomputeService` explicitly. Canonical filter set is now `{is_merged_into IS NULL, COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE}` — any header excluded from the register is excluded from the balance walk.

### Why an interceptor and not a trigger (revisited)

The trigger family broke four times under EF's batched `SaveChanges`: cascade-from-header DELETE ordering (mig 026, then again 094), override-on-posted_at bypass (mig 099), override-on-amount bypass (mig 101), and the EF-batch-ordering bug where a merge + postings-reshape in the same PATCH left a stale balance row pointing to the original Uncategorized account. Each fix added more trigger surface; each new surface introduced new edge cases.

An EF `SaveChangesInterceptor` avoids the entire failure class:

* Runs **once** per `SaveChanges`, after every DML statement in the batch has committed to the DB. One consistent `ChangeTracker` snapshot — no per-statement AFTER-trigger ordering question.
* Cascade-from-header DELETEs are handled by reading the doomed header's legs from the live DB in `SavingChangesAsync`, **before** the cascade has run, so the affected-accounts set survives.
* Recompute is a regular SQL function call from C#; it can't re-fire this interceptor.
* Lives in the codebase. F12-able, debuggable, PR-reviewable.
* The "invisibility" critique that applies to DB triggers doesn't bite here because every writer's class doc + method doc names the interceptor explicitly, and the interceptor itself is one file long enough to read in two minutes.
* Migration 097: rewrite `resolved_transactions` to project `header_seq` and rewrite `register_entry_keys` with `GROUP BY h.id` + `ORDER BY (posted_at DESC, seq DESC)` — entry_key is always `h.id`, no `COALESCE(txn_group_id, id)` fallback.
* `HoldingsRepository.GetAsync` orders cash-balance reads by `(PostedAt DESC, Seq DESC)`.
* `RegisterRepository` cursor codec: `{ PostedAt, Seq, EntryKey }` (replaces `CreatedAt`); `ResolveCursorForHeader` groups by `HeaderId`.
* `TxnLegRow` (Api + Importer) drops its `BalanceAfter` property; importer mappers and `TransactionsRepository` stop carrying it through the INSERT.
