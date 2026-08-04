# 0038 — Resolve `ingest_security_id` dynamically via view JOIN

* Status: Accepted
* Date: 2026-06-08
* Related: ADR-0031 (ingest-provider pattern), ADR-0032 (triggers
  last resort), migration 076 (original `ingest_security_id`
  column), migrations 113/114 (OFX investment prefill rail)

## Context

ADR-0031 Phase 3c introduced two columns on `txn_headers`:

* `ingest_action_hint` — the classifier's action guess (Buy / Sell /
  Dividend / …). Authoritative on the header; only the classifier
  produces it; no cross-row relation exists for it elsewhere.
* `ingest_security_id` — the classifier's resolved security id,
  looked up against `provider_security_mappings` at ingest time.

The second column is structurally different: its value is **derived
from another table**. `provider_security_mappings.(ledger_id,
provider_key, provider_security_id)` is the authoritative
`→ security_id` map. Storing the resolved id on the header is a
denormalized snapshot of that relation.

The denormalization is observable as a cluster of follow-on debt
that surfaced during the OFX investment-prefill slice
(migrations 113 / 114):

1. **Out-of-date snapshots after a mapping changes.** Every Accept
   of a never-before-seen ticker writes a new mapping row. Headers
   imported earlier still carry `ingest_security_id = NULL` — the
   editor reads null and shows an empty Security picker on every
   sibling row of the same ticker, even though the mapping now
   exists.

2. **A backfill side-effect inside the repo.**
   `ProviderSecurityMappingsRepository.UpsertAsync` was extended
   (PR #166 follow-up) to run an `ExecuteUpdateAsync` over
   `txn_headers` after every mapping insert, rewriting
   `ingest_security_id` on every header with a matching ticker hint.
   This is action-at-a-distance — one upsert mutates N unrelated
   rows — the same anti-pattern ADR-0032 retires for DB triggers,
   reproduced at the repo layer.

3. **Re-link requires a second backfill.** If a mapping is ever
   pointed at a different security (user-driven re-link), every
   stored snapshot is stale until the backfill re-runs.

4. **SPA cache staleness compounds the above.** The windowed
   register cache holds the column verbatim. A save that triggers
   a server-side backfill on N sibling rows never propagates back
   to the SPA's cache — the editor must source fresh per-header
   data on open just to see the column's current value.

The first three are direct consequences of the denormalization;
the fourth is a consequence of the first three.

## Decision

Drop `txn_headers.ingest_security_id`. Resolve the column **in the
`resolved_transactions` view** via a `LEFT JOIN` against
`provider_security_mappings`:

```sql
LEFT JOIN provider_security_mappings psm
       ON psm.ledger_id           = h.ledger_id
      AND psm.provider_key        = h.provider_key
      AND psm.provider_security_id = h.ingest_security_ticker_hint
-- expose psm.security_id AS ingest_security_id
```

The mapping table is the single source of truth. The view derives
the resolved id at read time. The DTO contract (`ingestSecurityId`
on `ResolvedTransactionDto`) is unchanged — only its source flips
from "stored on the header" to "resolved by the view."

### What stays

* `txn_headers.ingest_security_ticker_hint` (mig 114) — the
  provider-stamped raw identifier. Authoritative on the header
  (provider wrote it; not derivable from anything else).
* `txn_headers.ingest_action_hint` (mig 076) — same story: the
  classifier's action guess. Header-owned.
* `txn_headers.ingest_shares` / `ingest_unit_price` / `ingest_fee`
  (mig 113) — OFX-wire header-level data. Header-owned.
* `provider_security_mappings` — the authoritative table.
* The editor's existing `providerSecurityHint` flow on Accept (the
  SPA passes the ticker hint to the save endpoint; the server
  upserts the mapping). Unchanged.

### What goes

* The `txn_headers.ingest_security_id` column + its composite FK
  to `securities` + its partial index.
* The `IngestSecurityId` property on the `TxnHeaderRow` EF entity
  and its column mapping in `AppDbContext`.
* The `IngestSecurityId = ingestSecurityId` assignment on both
  insert sites in `IngestOrchestrator` (RunPullAsync,
  RunFileAsync).
* The `ExecuteUpdateAsync` backfill block in
  `ProviderSecurityMappingsRepository.UpsertAsync`.
* The `TryResolveSecurityIdAsync` call in the orchestrator's
  insert path — no longer needed; the view resolves on read.

### What stays the same

* `ResolvedTransactionView.IngestSecurityId` (the EF entity for
  the view) — the property maps to the same column name; the view
  just sources it differently now.
* `RegisterRepository.Project(...)` — still passes `r.IngestSecurityId`
  through to the DTO.
* `ResolvedTransactionDto.IngestSecurityId` — unchanged.
* SPA `ResolvedTransactionDto.ingestSecurityId` — unchanged. The
  editor's pre-fill path reads it identically to before.

## Consequences

### Positive

* **One source of truth.** `provider_security_mappings` is the
  only place `(provider, ticker) → security_id` lives.
* **Re-link is instant.** Updating a mapping row changes the
  resolved value for every header that matches, visible on the
  next read. No backfill, no replication lag.
* **No repo-layer triggers.** `UpsertAsync` is one row in, one
  row out. Aligned with ADR-0032's "explicit code at the call
  site" principle.
* **Smaller surface.** One fewer column, one fewer FK, one fewer
  index, one fewer code path to keep coherent during migrations.

### Negative

* **View has one more LEFT JOIN.** `resolved_transactions` already
  carries seven LEFT JOINs (overrides, counterparty, accounts,
  securities, balances). Adding an eighth on
  `provider_security_mappings(ledger_id, provider_key,
  provider_security_id)` — which is a covered UNIQUE constraint —
  is a small index seek per row. Real-data register pages
  (~3000 rows, paged 30 at a time) won't notice.
* **No "snapshot of what the user mapped at ingest time" column.**
  Headers ingested before a mapping existed used to show
  `ingest_security_id = NULL`; after the user mapped the ticker
  and the backfill ran, they showed the new id. Both states are
  defensible as a "history." With the dynamic JOIN there is no
  history — the resolved id is always the current mapping's id.
  This is the right contract: the user's intent is "this ticker
  means FUNDX" and that intent should retroactively apply to
  every row carrying that ticker, including past ones. (If a
  history is ever needed, `provider_security_mappings.created_at`
  + `txn_headers.created_at` provide enough information to
  reconstruct it without a stored column.)

### Migration

Single forward-only DDL migration (115) that:

1. Drops `resolved_transactions` (Postgres won't drop a column
   the view references).
2. Drops `txn_headers.ingest_security_id` (cascades the composite
   FK from mig 076 and the partial index `idx_txn_headers_
   ingest_security_id`).
3. Recreates `resolved_transactions` with the `LEFT JOIN
   provider_security_mappings psm ON …` and projects
   `psm.security_id AS ingest_security_id`.

No backfill, no data movement — the column was a snapshot of the
relation the view now joins.

## Alternatives considered

* **Keep the column, drop the repo-layer backfill, accept a brief
  stale-id window after Accept.** Rejected: the stale window was
  the user-visible bug that motivated the rethink. Trading "the
  picker is briefly blank" for less code isn't an improvement.

* **Materialized view refreshed on mapping change.** Adds caching
  for no measurable gain on real-data sizes; the JOIN cost we'd
  be avoiding is already inside the view, which is itself reads
  on every register query. Recomputing the materialization on
  every mapping change is the backfill problem renamed.

* **Trigger on `provider_security_mappings` to update
  `txn_headers.ingest_security_id`.** Same denormalization,
  with a trigger replacing the ExecuteUpdateAsync. Conflicts
  directly with ADR-0032.

* **Compute `ingest_security_id` in the SPA from a separate
  mappings fetch.** Pushes cross-table resolution to the client,
  multiplies round-trips, and forces every SPA reader of the
  field to repeat the same join logic. The view is the right
  layer.
