# 0048 — Live/template data-layer view abstraction (reminders foundation)

* Status: Accepted
* Date: 2026-06-12
* Refines: ADR-0047 (reminders / recurring transactions / calendar)
* Related: ADR-0030 (`resolved_transactions` register surface), ADR-0032 /
  0034 (recompute as interceptors, not triggers), ADR-0046 (read-path
  perf), ADR-0010. Migrations: 103 (`is_hidden` recompute predicate), 049
  / 072 (composite-FK coherence + `ledger_id` denormalization), 120
  (denormalized posting counts), 122 (`resolved_transactions`).

## Context

ADR-0047 decided a reminder's transaction is a real `txn_header` +
`txn_legs` flagged `is_recurring_template`, with the live-vs-template
separation enforced "in the view layer." This ADR pins down **how** — the
view layering, what the recompute reads, the read/write split, and how the
existing reminders convert — capturing the *why* for each, because the
reasoning is the load-bearing part.

Guiding principle (the user's): the raw transaction tables are encapsulated
behind a view abstraction. **Reads never name the raw partitioned tables;
the live/template partition is enforced structurally, in one place.**

## Decisions

### D1 — Layered views; the partition lives on ONE header-level view

- **Layer 0** — raw `txn_headers` / `txn_legs` (+ `txn_header_account_balances`).
- **Layer 1** — light, plain `SELECT … WHERE`, inline-able partition views:
  `live_txn_headers` / `template_txn_headers` = `WHERE [NOT] is_recurring_template`.
- **Layer 2** — composed views: `resolved_transactions` (register/reports)
  and `recurring_reminders` (the reminders surface), built **on top of** the
  Layer-1 views.

**Why a header-level view only (no `live_txn_legs`, no leg flag):** a leg
belongs to exactly one header, so any consumer that joins `txn_legs` to
`live_txn_headers` drops template legs automatically — a template header
isn't in the view, so the inner join excludes its legs. The partition is
enforced **once**, on the header; legs inherit it through the join. This
avoids a leg-level join-view (which wouldn't be auto-updatable) and any
denormalization of the flag onto legs.

### D2 — All reads go through views; the balance + holdings recompute read `live_txn_headers`

**Why not the raw tables:** encapsulation — no consumer names the raw
partitioned tables, so the template exclusion cannot be forgotten.

**Why not `resolved_transactions` (the Layer-2 fat view):** it LEFT JOINs
`txn_header_account_balances` — the table the balance recompute *rewrites* —
so a recompute reading it would be self-referential and fragile; and it
carries many joins (overrides, the counterparty self-join, securities, the
`tags` array subquery, `account_path()`, provider mappings), which would
regress the recompute hot path (a per-account loop + a full-ledger one-shot;
the ADR-0046 perf surface).

**Why the light `live_txn_headers` works where the fat view doesn't:** it's
a plain single-table filter view, so Postgres **inlines** it — view-on-view
plans identically to the base-table query (to be confirmed via the ADR-0046
full-account `EXPLAIN`) — and it has no `thab` join, so no circularity.

This **replaces** what would otherwise be a mig-103-style explicit
`AND NOT is_recurring_template` predicate duplicated in each recompute
function: the exclusion now lives once, in the view, and the recompute
inherits it. (`is_hidden` / `is_merged_into` stay explicit per-row filters
in the recompute — they are contextual axes the register applies
situationally, not the hard template partition.)

### D3 — Reads via views, writes via tables (command/query split)

**Why writes can't go through the views:** EF maps view-bound entities
(`ToView`) as **read-only** — it emits no INSERT/UPDATE/DELETE for a view.
And a leg-level live view would be a join view (not auto-updatable) without
INSTEAD OF triggers, which we avoid (triggers-last-resort, ADR-0032).

**Why this is not a leak in the abstraction:** the writer chooses the
partition by setting `is_recurring_template` on the header it inserts; the
views partition on the read side. The recompute interceptor's ChangeTracker
snapshot also operates on table-mapped entities. So the **write model** =
tables + EF + the recompute interceptor; the **read model** = views.
A mutation that first queries to pick rows (e.g. bulk-delete) resolves its
row set through `live_txn_headers` (templates unselectable), then executes
the write against the table.

**Refinement (manual-authoring slice):** "the recompute reads the view, so
templates are excluded" holds for the *balance* recompute — `thab` rows come
only from an `INSERT … SELECT` over `live_txn_headers`. But the **holdings**
path has a second enqueue step that runs *before* the view filter:
`HoldingsRecomputeInterceptor` builds the affected-`(account, security)` set
from the ChangeTracker's leg entries, and `recompute_holdings_cost_basis` has
an unconditional auto-create branch for any `(account, security)` passed to it.
So enqueuing a template's holdings-side leg would auto-create a spurious
zero-qty `holdings` row — a partition leak the view layer alone does **not**
catch. The interceptor therefore explicitly skips legs whose header is
`is_recurring_template` (it reads the flag from the same ChangeTracker — every
template write keeps the header tracked alongside its legs). The rule: any code
that *pre-computes* recompute work from raw leg/header changes (not via the
view) must replicate the template exclusion.

### D4 — No `is_recurring_template` on `txn_legs` now; reserve it as a constraint-backed option

**Why not now:** D1 means legs follow the header via the join — no consumer
reads legs without a header join *and* needs the partition, so a leg flag
earns nothing today.

**Why it is clean if ever needed:** a composite FK
`txn_legs(header_id, is_recurring_template) → txn_headers(id, is_recurring_template)`
makes the leg flag **structurally** equal its header's (set-once at insert,
no trigger), consistent with the existing `ledger_id` (mig 072) and
posting-count (mig 120) denormalizations on `txn_legs`. A reserved tool,
not a debt.

### D5 — `txn_header_account_balances` is not given its own view

**Why:** it is already encapsulated — APIs read `resolved_transactions`,
which exposes `balance_after` / `net_amount` from its `thab` join; nothing
reads `thab` directly. `thab` is the recompute's materialized *output*,
internal to the balance subsystem, and holds only live balances by
construction (templates never enter the recompute). A standalone
pass-through view would have no reader and no partition to apply. If a
future consumer needs raw per-(header, account) balances, add the view
then.

### D6 — Existing reminders convert via re-import, not in-migration SQL (option B)

**Why not in-migration SQL (option A):** a template must be a fully-valid
`txn_header` + `txn_legs`, and `txn_headers` is a large, evolved table — the
NOT-NULL set, the `provider_key`-iff-not-manual CHECK, the mig-120
denormalized posting counts — and the leg construction carries a
source/target sign convention plus a target-null → Uncategorized fallback.
The importer's C# already encodes all of this. Hand-building it again in
migration SQL would be a **second, divergent construction path** for the
same thing (duplication; risk of mis-signing the real reminders).

**Why re-import works:** every existing `recurring_transactions` row is
`origin='moneydance_import'` with an `external_id` and is idempotently
re-importable. So the migration reshapes the schema (adds the new columns,
drops the denormalized shape columns); the **rewritten importer
re-materializes** each reminder as a template header+legs on the next MD
import (idempotent via `external_id`), through the single validated
construction path. No data loss; one re-import (already part of the MD
refresh workflow). Rejected: A (fragile / duplicated) and C (a one-time C#
startup backfill — a new pattern + a two-phase column drop). B reuses the
existing importer + import flow with the least new surface.

## Consequences

### Positive
- Encapsulation: no raw-table reads; the live/template partition is a
  single structural point (`live_txn_headers`).
- The recompute stays lean and non-circular.
- One construction path for reminder templates (the importer).
- No denormalization, no triggers.

### Negative / accepted
- After the reshape and before the next re-import, an existing reminder row
  has null `rrule` / `template_header_id` — it is dormant (it has no
  template header, so it never appears in `recurring_reminders`) until the
  re-import re-links it via `external_id`. One re-import resolves it.
- Routing **every** existing direct-`txn_headers` read through
  `live_txn_headers` is broader than this slice → follow-up. This slice
  converts the readers that must exclude templates (register view, both
  recompute functions, bulk / `scheduled` predicates).

## Test plan
- View-on-view does not regress the ADR-0046 full-account scan (`EXPLAIN`).
- A template header + legs produces **zero** `txn_header_account_balances`
  / `holdings` rows; firing an occurrence produces exactly the rows a
  hand-entered transaction would.
- Re-import materializes valid templates idempotently from the MD export.
