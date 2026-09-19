# 0076 — Register filter: a single SQL source of truth

Status: Accepted
Date: 2026-07-14
Relates: [ADR-0034](0034-header-walk-running-balance.md), [ADR-0036](0036-originating-vs-target-register-entries.md) · migrations 164 (filter), 166 (sort), 167 (this)

## Context

The register filter predicate — search / date / amount / security / tag /
category / status — was written **twice**:

- **SQL**, in `register_entry_keys` (mig 164, extended by 166), for the windowed
  keyset page. This one is non-negotiable: the page is a sliding window over a
  cursor, so the client can't filter what it hasn't loaded (that was bug #322);
  the filter must run server-side inside the cursor walk.
- **LINQ**, in `RegisterRepository.ApplyRegisterFilterPredicates`, for the two
  full-set aggregates that don't paginate — the date-rail buckets
  (`GetIndexBucketsAsync`) and the status-count badges (`GetStatusCountsAsync`) —
  plus a third consumer, the bulk **select-all** intersection in
  `BulkTransactionsRepository`.

The two copies were kept in lockstep **by hand**. Migration 165 existed *solely*
to mirror the `reconciling` status arm into the SQL side after it was added to
the LINQ side. The status precedence (scheduled › pending › recon) was encoded in
three places (SQL arm, LINQ arm, and the in-memory bucketing in the counts). And
the copies had already drifted in one subtle way: the SQL used calendar-date
comparison (`posted_at::date`), the LINQ used a UTC-instant (`PostedAt >= midnight
UTC`) — identical only while the DB session is UTC (it is today), divergent at
day boundaries otherwise. A 2026-07-14 review flagged the whole arrangement as a
standing drift hazard rather than a live bug.

## Decisions

### D1 — One filter primitive; every consumer composes over it (mig 167)

`register_filtered_entries(accounts, ledger, hidden, …filters…) RETURNS SETOF
resolved_transactions` is the **single definition** of the register filter. It
applies visibility + ledger/account scope + all filter dimensions and returns the
matching rows (per-leg; an entry appears iff any of its legs match — the
entry-key derivation is constant across a header's legs, so a per-leg filter
gives correct entry-level semantics).

- `register_entry_keys` (the page) `SELECT`s **FROM** the primitive, then adds the
  entry-key `GROUP BY` + dynamic sort + keyset cursor + `LIMIT` — the
  pagination-only concerns stay there.
- The rail buckets, the status counts, and the select-all intersection call the
  primitive via `HasDbFunction` (LINQ) and do their own aggregation in C#.
- `ApplyRegisterFilterPredicates` — the LINQ twin — is **deleted**.

This collapses the duplication: the filter predicate lives once (SQL); the only
remaining C# status logic is the counts' *bucketing* (a genuinely different
operation — it partitions a set, it doesn't filter it), and the date semantics
are now single-sourced on `posted_at::date`.

#### D1a — The account scope is a SET, not one account (mig 226, amendment)

Both functions took `p_account_id uuid`. A **parent category has no postings of
its own** — they all sit on its children — so a register that could only name
one account could never show a category's own money: opening "Taxes" showed an
empty register while the Categories tree, the sidebar and the budget all
reported five figures against it. The scope is therefore `p_account_ids uuid[]`,
and the single-account case passes an array of one.

- The predicate went from `rt.account_id = $1` to `rt.account_id = ANY($1)`.
  Every other use of the parameter is an `IS NULL` / `IS NOT NULL` test, which
  reads identically on an array, so `NULL` still means *every visible account in
  the ledger*.
- **Not** an added `p_account_ids uuid[] DEFAULT NULL` beside the scalar: two
  ways to say the same thing invites a silent precedence question, and a changed
  parameter list under `CREATE OR REPLACE` creates an **overload** — every
  existing call then becomes ambiguous rather than failing loudly. Both
  functions are DROPped by full signature and recreated (the pattern mig 171
  established for this pair).
- Widening BOTH together is what keeps D1 true: the page, the rail, the counts
  and select-all scope identically because they share the primitive.
- `= ANY(array)` on a one-element array plans as an equality, so the register's
  hottest query is unchanged; a category subtree is tens of rows, not thousands.

Who chooses the set is the API's business, not the function's:
`RegisterRepository.ResolveScopeAsync` expands a category to its descendants
only when the caller asks (`include_subcategories`), and hands back a
one-element array otherwise.

### D2 — The primitive must inline (verified, not assumed)

The primitive is a single-`SELECT` `LANGUAGE sql STABLE` function, which the
Postgres planner **inlines** into the caller. This is load-bearing: if it were an
optimization barrier, the page would materialize full filtered history before the
`LIMIT` — a regression on the app's core read path. Verified on real data
(~125K-leg ledger) with `EXPLAIN`: the plan composed over the primitive is
**identical** to the pre-167 inline plan — same cost, same node tree, no
`Function Scan` barrier, `LIMIT` selectivity and the account index scan preserved.

### D3 — `p_hidden` is nullable: NULL = both visibility sides

The page and rail pass a concrete `TRUE`/`FALSE` (one visibility side). The
status counts need both (they count a Hidden bucket) → two calls. The select-all
needs both and applies visibility scope in its own outer query → passes `NULL`.
So `p_hidden` is `(p_hidden IS NULL OR is_hidden = p_hidden)`.

## Consequences

- Adding a filter dimension is a one-place change (the primitive); the page, rail,
  counts, and select-all inherit it. The `register_entry_keys`↔LINQ hand-sync
  (and the class of bug mig 165 patched) is gone.
- The three surfaces provably agree: `RegisterFilterConsistencyTests` asserts the
  page entry count == rail bucket total == status-counts `All` under a filter.
- The status counts now issue two primitive calls (visible + hidden) instead of
  one view scan. Counts is not a hot path (badge refresh), and each call is a
  filtered aggregate; acceptable.
- `RETURNS SETOF resolved_transactions` binds the primitive to the view's
  rowtype — a future `CREATE OR REPLACE VIEW` that changes columns must recreate
  the function (standard for SETOF-view functions).
- The focus/anchor (`starting_at`) path resolves its anchor **through** the
  primitive too (`ResolveCursorForHeaderAsync`), so a focused row the filter
  excludes is never pinned to the top; a non-matching anchor falls through to
  the most-recent *filtered* page (not an empty one). The SPA complements this
  by clearing `?focus=` when a filter changes. (Follow-up to the initial slice —
  the case where a scheduled non-matching row showed under a category filter.)
