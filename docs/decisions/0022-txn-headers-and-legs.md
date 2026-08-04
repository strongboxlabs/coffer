# 0022 — Transactions normalize into headers and legs

* Status: Accepted (complete)
* Date: 2026-05-11
* Supersedes: [ADR-0019](0019-symmetric-postings.md)
* Refines: [ADR-0017](0017-account-discriminator.md), [ADR-0018](0018-investment-and-cross-account-translation.md)

> **Implementation complete (2026-05-12).** The normalised schema is
> live end-to-end. Migration 025 (Phase 2) retargeted
> `lots.transaction_id` → `lots.leg_id` and
> `merge_candidates.{incoming,existing}_txn_id` → `*_header_id`, then
> dropped the legacy `transactions` / `transaction_overrides` /
> `transaction_tags` tables plus the ADR-0019 symmetric-pair trigger.
> Investment lots write through `BulkReplaceLotsAsync` (thousands of lots
> populated on a large real-world ledger). JIT-off on `coffer_app` stays
> (PR #45) — the new view's join count keeps the plan above
> `jit_above_cost`, so the role-level override is the right call for
> now; see the perf follow-up below for paths to retire it.

## Context

ADR-0019 collapsed every flow onto the `transactions` table and paired
both sides of every posting via `counterparty_id`. The shape was clean
for "what's the other side?" but pushed every event's *envelope*
metadata — payee, memo, date, status, check_number, import_source,
is_pending, is_user_defined — onto every leg as a duplicated cell. A
14-leg paycheck wrote those nine fields 28 times. Edits had to refresh
each cell in lockstep; the importer had to remember to populate them
identically; the resolved view re-projected the same identical
duplicates per row.

The smell was tolerable while groups were "UI sugar" (ADR-0019 Rule 3).
It became structural friction when the product needed *group-level*
state that doesn't have a per-leg meaning:

- **Reconciliation status.** A user reconciling a paycheck is reconciling
  the whole event, not 14 independent legs.
- **Online-match status.** OFX/SimpleFIN match status is an attribute of
  the incoming feed event, not its legs.
- **Group memo.** MD's `txn.memo` ("Electronic/ACH Credit") is the event's
  own descriptor, distinct from each leg's per-split memo ("Salary",
  "Federal Tax", …). Until 2026-05-11 the importer threw it away because
  no column carried it; surfacing it on `leg_index=0` only created a
  semantic asymmetry (`feed_memo` would mean different things on different
  legs of the same event).

Each future group-level field would have needed its own column on
`transactions` (duplicated N times) or a side table that grew alongside
the main one. The denormalized shape was a 250-line patch with a long
trail; the right answer is to model the envelope where it belongs.

A spot-check after PR #41's perf work confirmed a secondary benefit:
the RLS chain `transactions → accounts → user_ledger_grants` produced a
plan with 134 inlined functions that triggered ~500ms of JIT compilation
per query. Hoisting `ledger_id` directly onto the header collapses that
chain to one hop on header reads and two on leg reads.

The user's product call: **normalize into a `txn_headers` row for the
event and `txn_legs` rows for the per-account postings; pay the
migration cost once.**

## Decision

### Rule 1 — Every event is one `txn_headers` row

`txn_headers` carries the event's user-facing identity and group-level
state. One row per Moneydance txn (including single-split everyday
purchases — no special case). `external_id` is the MD txn id alone, no
leg suffix; uniqueness is at the header.

```sql
CREATE TABLE txn_headers (
    id                       UUID PRIMARY KEY,
    ledger_id                UUID NOT NULL REFERENCES ledgers(id),
    origin                   TEXT NOT NULL,           -- 'moneydance_import', 'user', 'simplefin', ...
    external_id              TEXT,
    payee                    TEXT,
    memo                     TEXT,
    posted_at                TIMESTAMPTZ NOT NULL,
    transacted_at            TIMESTAMPTZ,
    status                   TEXT,
    check_number             TEXT,
    is_pending               BOOLEAN NOT NULL DEFAULT FALSE,
    is_user_defined          BOOLEAN NOT NULL DEFAULT FALSE,
    is_hidden                BOOLEAN NOT NULL DEFAULT FALSE,
    is_merged_into           UUID REFERENCES txn_headers(id),
    import_source            TEXT,
    online_match_status      TEXT,
    reconciled_at            TIMESTAMPTZ,
    reconciled_by_user_id    UUID REFERENCES users(id),
    created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (ledger_id, external_id)
);
```

`reconciled_at` + `reconciled_by_user_id` are structured columns rather
than a single `reconciliation_status` text field — the cost of two
columns vs one is trivial and we'd rather have the audit trail than
re-derive it from a status string later. In-progress reconciliation
states (`pending_reconciliation`, etc.) live in `status` until they
either commit (set the timestamp) or roll back (clear it). Same shape
the rest of the app uses for "did X happen, when, by whom."

### Rule 2 — Every posting is two `txn_legs` rows on different accounts

A posting is an event's impact on a pair of accounts. Each side of the
posting is one `txn_legs` row referencing the same header with the same
`posting_index`. Their `amount` values sum to zero (same-currency
invariant). A single-split event has 1 posting and 2 legs; a 14-split
paycheck has 14 postings and 28 legs.

```sql
CREATE TABLE txn_legs (
    id                  UUID PRIMARY KEY,
    header_id           UUID NOT NULL REFERENCES txn_headers(id) ON DELETE CASCADE,
    account_id          UUID NOT NULL REFERENCES accounts(id),
    posting_index       INTEGER NOT NULL,
    leg_memo            TEXT,                    -- per-split note ("Salary", "Federal Tax", ...)
    amount              NUMERIC(18, 4) NOT NULL,
    balance_after       NUMERIC(18, 4),
    investment_action   TEXT,
    security_id         UUID REFERENCES securities(id),
    quantity            NUMERIC(18, 8),
    unit_price          NUMERIC(18, 8),
    commission          NUMERIC(18, 4),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (header_id, posting_index, account_id)
);
```

The unique `(header_id, posting_index, account_id)` plus a CHECK that
each posting_index appears exactly twice within a header enforces the
two-legs-per-posting invariant. "Other side of this leg":

```sql
SELECT * FROM txn_legs
WHERE header_id = $1 AND posting_index = $2 AND id != $3;
```

One row by invariant. Same expressiveness as ADR-0019's
`counterparty_id` lookup, derived structurally rather than denormalized.

### Rule 3 — Header memo and leg memo are distinct fields

`txn_headers.memo` is the event's umbrella memo (MD's `txn.memo`:
"Electronic/ACH Credit", "Birthday gift", check memo line). It's what a
single-leg coffee purchase shows in the register's memo column.

`txn_legs.leg_memo` is the per-split note (MD's `split.desc`: "Salary",
"Federal Tax", "Long Term Disability Ins (after tax)"). It surfaces
when the user expands a multi-split entry.

For a single-leg event, `leg_memo` is typically NULL and the register
falls back to header memo — same UX as today's coffee row. For a
multi-split, both columns carry value and both render where appropriate.

### Rule 4 — Investment metadata lives on the leg

`security_id`, `quantity`, `unit_price`, `commission`,
`investment_action`, `balance_after` are per-leg attributes (the
holdings-side leg carries shares; the cash-side leg carries dollars).
They stay on `txn_legs`, NULL on legs that don't apply.

`balance_after` keeps the per-leg-per-account running balance from
ADR-0004. The trigger continues to fire on `txn_legs` inserts/updates;
the chain is `txn_legs.account_id` directly — no JOIN through the
header.

### Rule 5 — Overrides split into header and leg tables

Replaces the single `transaction_overrides` table with two:

```sql
CREATE TABLE txn_header_overrides (
    header_id UUID PRIMARY KEY REFERENCES txn_headers(id) ON DELETE CASCADE,
    payee TEXT, memo TEXT, posted_at TIMESTAMPTZ, transacted_at TIMESTAMPTZ,
    status TEXT, is_hidden BOOLEAN,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE txn_leg_overrides (
    leg_id UUID PRIMARY KEY REFERENCES txn_legs(id) ON DELETE CASCADE,
    leg_memo TEXT, amount NUMERIC(18, 4),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

The split is symmetric with the underlying tables. Editing a leg's
amount is a leg override; editing the payee is a header override.
COALESCE in the view picks override over feed value as before.

### Rule 6 — Tags live on the header

`txn_header_tags` replaces `transaction_tags`. Tags describe the event;
a multi-split paycheck's "vacation" tag applies to all 14 legs by virtue
of being on the header.

### Rule 7 — `txn_group_id` is gone

The header *is* the group. Single-split events and multi-split events
have one shape; the importer no longer special-cases `groupId =
emittable.Count > 1 ? Guid.NewGuid() : null`. The resolved view exposes
`legs_count` derived from the leg cardinality so the UI can still render
"— N splits —" for the collapsed parent.

### Rule 8 — RLS on the header; legs inherit

```sql
ALTER TABLE txn_headers ENABLE ROW LEVEL SECURITY;
CREATE POLICY hdr_ledger_visible ON txn_headers
    FOR ALL TO coffer_app
    USING (ledger_id IN (
        SELECT ledger_id FROM user_ledger_grants
        WHERE user_id = current_setting('app.user_id')::uuid
    ));

ALTER TABLE txn_legs ENABLE ROW LEVEL SECURITY;
CREATE POLICY leg_via_header ON txn_legs
    FOR ALL TO coffer_app
    USING (header_id IN (SELECT id FROM txn_headers));
```

`txn_headers` reads need one hop (`ledger_id → user_ledger_grants`).
`txn_legs` reads need two (`header_id → txn_headers.ledger_id →
user_ledger_grants`). Both are shorter than the
`transactions → accounts → user_ledger_grants` chain in ADR-0020 Phase D,
which is what triggered the JIT regression patched in PR #41.

**Phase 2 update (2026-05-12):** Measured after the cut-over. The new
`register_entry_keys` walks `resolved_transactions`, which under
ADR-0022 joins txn_headers + txn_legs + 2 override tables +
self-LEFT-JOIN on legs for the counterparty + accounts + the
`account_path` recursive function. Postgres compiles ~256 functions
per query (vs 134 in the old shape — *more*, not less), so JIT
compilation still dominates wall time. Spot-check:

  register_entry_keys via psql, JIT on:  ~800ms
  register_entry_keys via psql, JIT off: ~150ms
  Full HTTP request, JIT off:            ~450ms warm

PR #45 restored the `ALTER ROLE coffer_app SET jit = off` workaround.
The simpler RLS chain still helped — pre-ADR-0022 register reads
were ~1.6s warm, post are ~450ms — but the JIT cost moved from the
RLS subqueries to the view's join structure rather than vanishing.
Future ADR could simplify the view (denormalise counterparty?), raise
`jit_above_cost` cluster-wide, or move some joins into the
RegisterRepository LINQ surface.

**Settled (ADR-0046 close-out, 2026-06):** all three were considered and
the question is now closed — `jit = off` *stays*. ADR-0046 removed every
per-row correlated subquery, after which the windowed page no longer
trips JIT (so the role setting is a no-op there), but the full-account
scan still trips it (282 functions) for ~70-100 ms of compile with no
execution benefit. Lifting the override would only regress reports;
counterparty denorm measured cheap (~2 ms, won't do). It is the
deliberate, measured optimum, not a workaround. See ADR-0046's close-out
and `docs/follow-ups.md` "View join cost".

## Consequences

**Positive**

- Group-level state has a home. Reconciliation, online-match, future
  group fields land on the header without column-count growth on legs.
- Edits are O(1) for header fields. Updating a payee touches one row,
  not 28.
- The header memo is no longer thrown away by the importer. Per-leg
  memos remain distinct.
- Single-split and multi-split events follow one code path. The
  importer, the view, the API, and the UI all stop branching on
  `txn_group_id IS NULL`.
- RLS plans collapse. Header reads are a single subquery hop. The
  hoped-for elimination of the JIT-off workaround didn't materialize
  (see Phase 2 update above) — the JIT cost moved from the RLS
  subqueries to the view's join structure — but warm-register latency
  dropped from ~1.6s to ~450ms anyway.
- "Other side of this leg" is structural, not denormalized. No
  symmetric-pair trigger needed to enforce that A↔B references stay in
  sync — they do by construction.

**Negative**

- Migration cost was real. Phase 1 shipped as PRs #44 (schema +
  importer + API EF + tests) and #46 (investment importer port);
  ~1700 LOC across ~30 files. Phase 2 (drops + lots/merge_candidates
  FK retarget) is still pending. ADR-0019's `counterparty_id` /
  `txn_group_id` / `leg_index` columns remain on the legacy
  `transactions` table — the new tables don't carry them — but the
  table itself is unused by the read path.
- Row count shifts. A simple coffee txn is now 3 rows (1 header + 2
  legs) where ADR-0019 had 2. Real-export expansion: an N-txn export →
  N headers + ~4N legs (a buy expands to a brokerage-cash + holdings
  pair; a divr to four legs) after both importer phases (the legs
  count is higher than the prior row count because
  each posting now has two leg rows whereas ADR-0019's "single-split"
  events had two `transactions` rows that played both roles).
- One more JOIN in every register read. Cheap (PK→FK) and the view
  materializes it once, but it's a real plan step.
- Phase 1's "two schemas coexist" period is real complexity. The
  legacy `transactions` table sits there with stale data; CI's
  truncate lists have to cover both old and new tables; readers have
  to know which surface is canonical (the new one). Worth the
  trade-off vs the Phase 2 work of retargeting lots+merge_candidates
  before the schema worked end-to-end.

**Breaking**

- DTO shape kept stable. The resolved-view rewrite preserved the
  column shape byte-for-byte, so `ResolvedTransactionDto` and every
  consumer kept compiling. The header-vs-leg semantic split is
  hidden inside the view; only writers care.
- All API integration tests that asserted against `transactions`-
  table rows reshape onto `txn_headers` + `txn_legs`.
  `SyntheticLedger.AddTransactionPairAsync` /
  `AddMultiSplitAsync` / `HideTransactionAsync` / etc. seed via the
  new schema and COALESCE-resolve leg-id → header-id for header-level
  operations.

## Migration strategy

Two-phase, **not** the big-bang originally planned. Justified because:

1. No production envs. The user's local DB and CI's per-run fixture are
   the only places this data exists.
2. The re-import path is well-exercised (3 reimports this session). Time
   cost: a few tens of seconds for a large export.
3. A phased dual-write would add 2-3× the migration work for migration
   mechanics that nobody benefits from.

**Phase 1 (shipped 2026-05-12, PRs #44 + #46):**
- Migration 022 creates `txn_headers` / `txn_legs` /
  `txn_header_overrides` / `txn_leg_overrides` / `txn_header_tags`
  alongside the legacy tables. RLS + indexes set up.
- Migration 023 rewrites `resolved_transactions` to project from the
  new tables, replaces `register_entry_keys`, and ports the running-
  balance trigger to fire on `txn_legs` (plus a header-side trigger
  for posted_at / is_merged_into changes).
- Migration 024 widens `txn_legs.investment_action` CHECK to match
  the widening migration 007 had applied to `transactions`.
- Importer rewritten (both `TransactionMapper` and
  `InvestmentTransactionMapper`) to emit headers + legs.
- API EF entities for the five new tables.

**Phase 2 (shipped 2026-05-12, migration 025):**
- Retargeted `lots.transaction_id` → `lots.leg_id` (FK to
  `txn_legs(id)`). Investment-importer lot insertion re-enabled;
  thousands of lots on a large real-world ledger.
- Retargeted `merge_candidates.{incoming,existing}_txn_id` →
  `*_header_id` (FK to `txn_headers(id)`) for the future sync
  pipeline.
- Dropped legacy `transactions` / `transaction_overrides` /
  `transaction_tags` plus the ADR-0019 symmetric-pair trigger and
  the pre-ADR-0022 running-balance trigger functions
  (`fn_recompute_balance_after`, `fn_trg_balance_after`).
- Re-pointed the RLS policies on `lots` and `merge_candidates` from
  the legacy tables to the new ones.
- `TransactionsRepository.BulkUpsertAsync` extended to return a
  proposed → persisted leg-id map alongside the existing header
  map; the lot writer uses it to remap `LegId` from the mapper's
  proposed id to whatever survived ON CONFLICT.

Phase 2 truncated existing `lots` and `merge_candidates` data on
the way in — those tables held references to the legacy
`transactions` ids that wouldn't survive the drop. The investment
importer's re-import repopulates lots; `merge_candidates` is a
short-lived review queue with no production data.

## Alternatives considered

- **Add `transaction_groups` side table only.** Carries the
  group-level fields (memo, reconciled_at, online_match_status) keyed
  by `txn_group_id`; existing duplicated columns stay duplicated.
  Cheaper now (~250 LOC) but the third pattern alongside (per-leg
  duplicated) and (per-leg actual) compounds — every future field
  re-prompts "where does this live?" Rejected as a conservative carve-
  out that quietly preserves the old denormalization.
- **Self-referential `parent_transaction_id` on `transactions`.** One
  row sometimes plays the parent, sometimes a leg. Mixed semantics in
  every read path, every query needs to filter parents in or out.
  Rejected.
- **Co-opt `leg_index=0`'s row as the parent.** No new table; leg 0's
  fields mean "parent metadata," other legs mean "leg metadata." Same
  asymmetry surfaced when we tried to put header memo on leg 0 alone:
  `feed_memo` would carry different concepts depending on `leg_index`,
  and leg 0's own per-leg memo would have nowhere to live. Rejected.
- **Full double-entry textbook model with explicit debits/credits.**
  Considered. Rejected because Coffer's sign-convention is per-account
  (positive = inflow on this account, negative = outflow) rather than
  the accounting-strict debit/credit pair. The signed-amount-per-leg
  model is simpler and already widely understood in MD-style ledgers.
