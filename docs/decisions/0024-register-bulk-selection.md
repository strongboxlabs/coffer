# 0024 — Register bulk-selection model (Gmail-style)

* Status: Accepted
* Date: 2026-05-15
* Companion to: [ADR-0019](0019-symmetric-postings.md), [ADR-0023](0023-ui-ux-interaction-conventions.md)
* Implements: window cap + FIFO eviction on the SPA register; predicate-based bulk operations on the API.

## Context

The register's sliding-window pagination (migration 031) bounds DOM
size via virtuoso but does not bound the in-memory `entries` array —
the window grows monotonically as the user scrolls. With the real
dataset (~41K transactions today, expected to double, mobile coming)
the unbounded-grow model isn't viable: heap pressure on phones,
slower re-renders on each append.

At the same time, the bulk-action footer's checkbox-driven selection
breaks once selection lives across an eviction. The pre-ADR-0024
selection model was `Set<string>` over leg ids:
- Selecting 50 rows then scrolling far enough to trigger eviction →
  some selected rows leave the window → footer still shows "50
  selected" but the user can't see them. Bulk action fires per-row
  PUTs on rows the user no longer sees.
- "Select all" via the column-header checkbox enumerated *visible*
  leg ids, never the whole account. For a register-scale dataset
  this is a useless gesture — the user can't realistically scroll
  through 40K rows to select them.

Both problems compound: a bulk action on a 40K-row account would
require enumerating 40K ids client-side, then firing 40K parallel
PUTs to the per-row endpoint. That's pathological — slow, brittle,
and hammers the API.

## Decision

Two coupled changes, shipped together because each is broken
without the other:

1. **Soft cap on the windowed register with FIFO whole-page
   eviction at the far edge from the load direction.** Constants
   in `src/Web/src/lib/useWindowedRegister.ts`:
   `MAX_ENTRIES = 1000`, `EVICTION_HYSTERESIS = 100`. The window
   sits in the [1000, 1100] band normally; eviction trips when
   total exceeds 1100, drops whole pages until back under
   MAX_ENTRIES + HYSTERESIS. Per-page cursor boundaries stay
   server-anchored so re-fetching an evicted page is exact —
   no client-side cursor synthesis.

2. **Bulk-selection model becomes a discriminated union**
   (`SelectionRequest` in [src/Api/Contracts/TransactionWriteDtos.cs](../../src/Api/Contracts/TransactionWriteDtos.cs)):

   ```
   { kind: 'explicit', headerIds: Guid[] }
   { kind: 'all',
     accountId?: Guid,
     statusFilter: 'all' | 'cleared' | 'uncleared' | 'scheduled',
     selectedAt: timestamp,
     excludeIds: Guid[] }
   ```

   Three new API endpoints take this shape:
   - `POST /api/ledgers/{id}/transactions/selection-summary` →
     `{ count, sumOnAccount }`. Server resolves the predicate;
     drives the footer's "N selected · Σ $X.XX".
   - `POST /api/ledgers/{id}/transactions/bulk-recon-status` →
     atomic UPDATE on every header matching the predicate.
   - `POST /api/ledgers/{id}/transactions/bulk-delete` → per-row
     hard-vs-soft policy applied across the entire selection in
     one transaction.

   POSTs (not GETs) because the `excludeIds` payload can reach
   ~360 KB at the 10K-id cap (capped by `SelectionLimits.MaxIds`).

3. **`selectedAt` is the "moment of intent" anchor.** Server-side
   the `'all'` predicate gates on `created_at <= selectedAt`. Rows
   that arrive after that moment (manual entries the user creates
   while selection is active, SimpleFIN imports landing in the
   background) do NOT silently join the selection. Captures
   Gmail's "everything I had selected when I clicked the button"
   semantics in one server-side field — no client-side id tracking
   needed.

## Eight design questions, each pinned

| # | Question | Answer |
|---|---|---|
| 1 | Bulk delete is a footgun on 40K rows. Mitigation? | Typed confirmation when `count > 100`. User must type `delete <N>` exactly to enable Confirm. Single accidental click cannot catastrophically delete. |
| 2 | Filter changes during `'all'` mode — re-anchor, sticky, or reset? | **Reset to explicit-empty**. Switching filters is a fresh intent. Trying to "follow" the predicate silently is more confusing than just clearing. Hook resets on (ledgerId, accountId, statusFilter) change. |
| 3 | New transactions created during `'all'` mode — auto-selected? | **Not auto-selected**. `selectedAt` excludes them by construction. Imported rows (SimpleFIN) excluded by the same mechanism. |
| 4 | Where does the footer count + Σ come from? | **Always server**, debounced ~200ms in `useSelection`. Single source of truth across both modes and across window eviction; SPA never enumerates ids in `'all'` mode. |
| 5 | Filter change during `'explicit'` mode — drop filtered-out selections? | **Reset.** Same as #2. Predictable beats clever. |
| 6 | Header-checkbox visual when in `'all'` mode with full visible exclusion. | Live with indeterminate-looking-like-empty. Footer count is the source of truth — the user sees the "N selected" readout regardless of the visual checkbox state. |
| 7 | Partial failure during bulk action. | **All-or-nothing.** Server wraps the bulk UPDATE/DELETE in one Postgres transaction; one bad row rolls the whole bulk back with a 422. Easier to reason about than per-row partial success. |
| 8 | Optimistic UI in `'all'` mode (40K rows). | **Visible-only.** SPA walks `register.entries` (the loaded window), finds headers matching the selection, flips their badges optimistically. Off-screen rows surface the new status when they next enter the window. Honest, predictable, no fight with virtuoso. |

## Cap parameters

| Param | Value | Rationale |
|---|---|---|
| `MAX_ENTRIES` | 1000 | 10× the default page size. Generous on desktop (~500 KB heap) and safe on phones. |
| `EVICTION_HYSTERESIS` | 100 | Matches the page size. Prevents the "count drops to 930" jolt at the timeline tail when a partial page would push us a hair over MAX and trigger a full-page eviction. |
| `SelectionLimits.MaxIds` | 10,000 | Caps the HTTP body for `headerIds` / `excludeIds`. Past this point the SPA encourages the user to refine the filter instead. |
| Typed-confirm threshold | 100 | Aligns with the "more than a screenful" mental model — above this count, a single click could affect rows the user can't see, so we slow the gesture down. |

## Implementation map

- **DB**: no migration. Predicates compose over existing columns
  (`txn_headers.created_at`, `status`, `posted_at`, `external_id`,
  `is_hidden`, `is_merged_into`) and `txn_legs.account_id`.
  Bulk update/delete uses EF Core 8's `ExecuteUpdateAsync` /
  `ExecuteDeleteAsync` — one statement to Postgres, no row
  enumeration in C# (per [feedback_no_raw_sql_in_api](../../).

- **API**:
  - [src/Api/Contracts/TransactionWriteDtos.cs](../../src/Api/Contracts/TransactionWriteDtos.cs) — `SelectionRequest`, `SelectionSummary`, `BulkReconStatusRequest/Response`, `BulkDeleteRequest/Response`, `SelectionLimits`.
  - [src/Api/Db/Repositories/BulkTransactionsRepository.cs](../../src/Api/Db/Repositories/BulkTransactionsRepository.cs) — predicate builder + summary/update/delete methods.
  - [src/Api/Endpoints/TransactionsEndpoints.cs](../../src/Api/Endpoints/TransactionsEndpoints.cs) — three new POST handlers + shared `ValidateSelection`.
  - [src/Api/Errors/BusinessError.cs](../../src/Api/Errors/BusinessError.cs) — `selection-kind-invalid`, `selection-empty`, `selection-status-filter-invalid`, `selection-exclude-too-large`.

- **Web**:
  - [src/Web/src/lib/useWindowedRegister.ts](../../src/Web/src/lib/useWindowedRegister.ts) — eviction hysteresis + atomic `WindowState`.
  - [src/Web/src/lib/useSelection.ts](../../src/Web/src/lib/useSelection.ts) — discriminated selection state + debounced summary query.
  - [src/Web/src/components/ui/ConfirmDialog.tsx](../../src/Web/src/components/ui/ConfirmDialog.tsx) — `requireTypedConfirmation` + `isConfirming` props.
  - [src/Web/src/routes/ledgers/register/bank/BankRegisterPage.tsx](../../src/Web/src/routes/ledgers/register/bank/BankRegisterPage.tsx) — `useSelection` replaces `selectedIds: Set<string>`; bulk mutations call the new endpoints; header checkbox toggles via `selection.toggleAll`.

## Consequences

- The user can now meaningfully bulk-act on a whole account: one
  click on the column-header checkbox selects every matching row
  in `O(server query)` time, not `O(40K HTTP requests)`.
- Selection persists across scrolling, including across the
  window's eviction boundary. The user never silently loses
  selected rows.
- Heap on the SPA stays bounded — works on phones.
- The footer's "N selected" reflects a server count, not a client
  enumeration. Reliable across eviction, across mode, across
  filter changes.
- The user pays one server round-trip per checkbox click for the
  debounced summary. ~200ms window absorbs rapid interactions
  into a single call. Acceptable on local-network deploys; if a
  remote deploy ever lands the debounce window may need tuning.
- Bulk actions are all-or-nothing — a row that fails the DB
  CHECK rolls the whole transaction back. Surfacing the failed
  row to the user is a future task; for now, the user retries
  the bulk action with a refined predicate or fixes the
  underlying constraint.

## Alternatives considered

- **Eager-load all ids for "Select all".** Fetch every header id
  in the account into the SPA, then bulk-act per-id. Rejected:
  ~1.8 MB on the wire for 41K ids today (doubles with growth),
  and the bulk action still fires 40K parallel PUTs. Predicate-
  based is structurally better and only adds one new endpoint.
- **Skip selection cap altogether.** Let the user select 40K
  rows via a hypothetical client-side "select all visible" and
  scroll-through. Rejected: incoherent UX (user has to scroll
  the whole register), and still requires the API endpoints to
  not be 40K HTTP requests.
- **Per-row partial success on bulk action.** Server returns
  `{ updated: 39999, failed: [{id, reason}] }`. Rejected for v1:
  far more endpoint surface, harder to reason about transaction
  boundaries, and the per-row failure modes are mostly DB-CHECK
  edge cases the user can't usefully act on row-by-row. All-or-
  nothing is the cleaner contract; we can split failures out if
  it becomes a real pain point.

## Update (2026-06): extended to the investment register

Selection was bank-only at first. It now applies to **both**
registers with identical behavior:

- `useSelection` was already domain-agnostic (operates on
  `headerId` + `createdAt`, account-scoped) — reused as-is.
- The action bar is now the shared
  `register/shell/RegisterBulkActionBar` (lifted from the former
  bank-only bar), with an `extraActions` slot for domain-specific
  buttons. It renders unconditionally on both pages as the
  always-on "N rows loaded" status strip; the selection Σ + action
  buttons appear only once a row is checked.
- The `bulk-recon-status` / `bulk-delete` endpoints are
  domain-agnostic (they resolve a selection of headers), so
  investment reuses them — no new backend.

**Investment op set = set-recon-status + bulk-delete.** A
"bulk set security" op was considered and **dropped**: for
unaccepted rows the security is an automatic hint (ADR-0038
resolves it dynamically via `provider_security_mappings` — nothing
to assign by hand), and re-pointing an *accepted* transaction's
security is a separate migration concern, not a register bulk op.

A per-domain `RegisterBulkOp[]` descriptor was deferred: the two
domains' op sets are currently identical (recon + delete), so the
`extraActions` slot is sufficient parameterization. Introduce the
descriptor only when a domain needs a genuinely distinct op set.
