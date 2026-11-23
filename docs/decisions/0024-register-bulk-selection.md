# 0024 — Register bulk-selection model (Gmail-style)

* Status: Accepted (amended 2026-10-27 by [ADR-0108](0108-memo-and-tags-per-side.md): a selection is one register's, and read-only rows are left out or refused; amended 2026-11-03: the summary is a read, and a failed one draws no Σ; amended 2026-11-04: #2, a Filter edit that changes nothing keeps the selection; amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md): the Σ names its currency, and on a category's register in several currencies it converts)
* Date: 2026-05-15
* Companion to: [ADR-0019](0019-symmetric-postings.md), [ADR-0023](0023-ui-ux-interaction-conventions.md)
* Implements: window cap + FIFO eviction on the SPA register; predicate-based bulk operations on the API.

## Context

The register's sliding-window pagination (migration 031) bounds DOM
size via virtuoso but does not bound the in-memory `entries` array —
the window grows monotonically as the user scrolls. On a
long-kept ledger, whose transaction count only grows, and on
mobile, the unbounded-grow model isn't viable: heap
pressure on phones, slower re-renders on each append.

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
  through every row of a large account to select them.

Both problems compound: a bulk action on a large account would
require enumerating every id client-side, then firing one parallel
PUT per row to the per-row endpoint. That's pathological — slow, brittle,
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
| 1 | Bulk delete is a footgun on a whole large account. Mitigation? | Typed confirmation when `count > 100`. User must type `delete <N>` exactly to enable Confirm. Single accidental click cannot catastrophically delete. |
| 2 | Filter changes during `'all'` mode — re-anchor, sticky, or reset? | **Reset to explicit-empty**. Switching filters is a fresh intent. Trying to "follow" the predicate silently is more confusing than just clearing. Hook resets on (ledgerId, accountId, statusFilter) change. *(Amended 2026-11-04.)* An edit that leaves every filter dimension as it was is not a change: the Filter popover hands up no new filter for it, so the selection stays (typing in the security search before the text names a security, picking the category already set). |
| 3 | New transactions created during `'all'` mode — auto-selected? | **Not auto-selected**. `selectedAt` excludes them by construction. Imported rows (SimpleFIN) excluded by the same mechanism. |
| 4 | Where does the footer count + Σ come from? | **Always server**, debounced ~200ms in `useSelection`. Single source of truth across both modes and across window eviction; SPA never enumerates ids in `'all'` mode. |
| 5 | Filter change during `'explicit'` mode — drop filtered-out selections? | **Reset.** Same as #2. Predictable beats clever. |
| 6 | Header-checkbox visual when in `'all'` mode with full visible exclusion. | Live with indeterminate-looking-like-empty. Footer count is the source of truth — the user sees the "N selected" readout regardless of the visual checkbox state. |
| 7 | Partial failure during bulk action. | **All-or-nothing.** Server wraps the bulk UPDATE/DELETE in one Postgres transaction; one bad row rolls the whole bulk back with a 422. Easier to reason about than per-row partial success. |
| 8 | Optimistic UI in `'all'` mode (an entire large account). | **Visible-only.** SPA walks `register.entries` (the loaded window), finds headers matching the selection, flips their badges optimistically. Off-screen rows surface the new status when they next enter the window. Honest, predictable, no fight with virtuoso. |

## Cap parameters

| Param | Value | Rationale |
|---|---|---|
| `MAX_ENTRIES` | 1000 | 10× the default page size. Generous on desktop (~500 KB heap) and safe on phones. |
| `EVICTION_HYSTERESIS` | 100 | Matches the page size. Prevents the "count drops to 930" jolt at the timeline tail when a partial page would push us a hair over MAX and trigger a full-page eviction. |
| `SelectionLimits.MaxIds` | 10000 | Caps the HTTP body for `headerIds` / `excludeIds`. Past this point the SPA encourages the user to refine the filter instead. |
| Typed-confirm threshold | 100 | Aligns with the "more than a screenful" mental model — above this count, a single click could affect rows the user can't see, so we slow the gesture down. |

## Implementation map

- **DB**: no migration. Predicates compose over existing columns
  (`txn_headers.created_at`, `status`, `posted_at`, `external_id`,
  `is_hidden`, `is_merged_into`) and `txn_legs.account_id`.
  Bulk update/delete uses EF Core 8's `ExecuteUpdateAsync` /
  `ExecuteDeleteAsync` — one statement to Postgres, no row
  enumeration in C# (per [ADR-0005](0005-dapper-and-efcore.md) and
  the no-raw-SQL rule for `src/Api`).

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
  in `O(server query)` time, not one HTTP request per row.
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
  megabytes on the wire for a large account's ids (growing with
  the ledger), and the bulk action still fires one parallel PUT
  per row. Predicate-based is structurally better and only adds
  one new endpoint.
- **Skip selection cap altogether.** Let the user select every
  row of a large account via a hypothetical client-side "select
  all visible" and scroll-through. Rejected: incoherent UX (user
  has to scroll the whole register), and still requires the API
  endpoints to not be one HTTP request per row.
- **Per-row partial success on bulk action.** Server returns
  `{ updated: N, failed: [{id, reason}] }`. Rejected for v1:
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

## Amendment, 2026-10-27 — a selection is one register's, and read-only rows stay out (ADR-0108 D14)

Which side owns a transaction is stored (ADR-0108 D3, D5), so the bulk endpoints judge
ownership by the same flag the register marks a read-only row by
(`txn_legs.notes_is_read_only`), not by the posting counts. Three statements above no
longer hold:

- **`accountId?: Guid` is required.** Every bulk endpoint (`selection-summary`,
  `bulk-recon-status`, `bulk-delete`, `bulk-unhide`, `bulk-move-account`) refuses a
  selection without it with 422 `account-not-in-ledger` (`ValidateSelection`). There is no
  ledger-wide selection.
- **An explicit selection is not applied as given when it names a read-only row.**
  `bulk-delete`, `bulk-unhide` and `bulk-move-account` refuse it whole, before anything is
  written, with 422 `selection-includes-read-only`
  (`BulkTransactionsRepository.HasReadOnlyRowAsync`). An id that names no live transaction
  of the ledger is ignored, not refused. `bulk-recon-status` is the exception: a row's
  status is the account's own leg (ADR-0082), so it accepts read-only rows.
- **An `'all'` selection covers only the rows the register can edit**: the headers with a
  leg in the register's scope whose `notes_is_read_only` is false. That holds for delete,
  unhide, move and the summary's count, so a typed-confirm count is what gets changed.
  Recon status again excepted: a select-all sets it on the read-only rows the register
  shows as well.

## Amendment, 2026-10-28 — the footer counts read-only rows

The footer's count is every selected row, read-only rows included, with the read-only part
named beside it: `15 selected · 3 read-only`. With no read-only row in the selection it stays
`N selected`. This holds for select-all and explicit selections alike. No action's scope
changes; the amendment above still decides what each one reaches.

- **`selection-summary` returns `{ count, totalCount, readOnlyCount, sumOnAccount }`.**
  `totalCount` is every row the selection covers (for a select-all, every row the register
  shows under the view, the set recon status reaches), and `readOnlyCount` is how many of
  those the register shows read-only; both come from one statement, judged by the same
  `notes_is_read_only` rule. `count` keeps its meaning: the rows a bulk change acts on, so the
  typed-confirm and move counts are still what gets changed.
- **The Σ sums every selected row.** `sumOnAccount` covers the rows `totalCount` counts,
  read-only rows included, in both selection modes, so it always matches the count beside it.
  Each row contributes its own legs in the register's scope (with `includeSubcategories`, the
  sub-categories' legs too), the scope recon status reaches. Each leg is summed once, read off
  `txn_legs`.
- **A selection never covers a recurring template.** A reminder's template has legs on its
  accounts, but no register lists it, so neither selection mode matches one: not in the counts,
  not in the Σ, and not in what any bulk action reaches. An explicit selection naming one
  ignores it, as it ignores any id that names no live transaction.
- **An explicit selection's total is the ids it holds; its read-only part is the
  server's.** The summary's `totalCount` for an explicit selection is the ids it names, so
  the two agree. Its `readOnlyCount` is what the footer shows once the summary for that
  selection has arrived (`useSelection`'s `isSummaryCurrent`), because a selected row can
  sit on a page the window has evicted. While it is in flight, the footer counts the
  headers whose loaded rows are all `isReadOnly` (`selectionFooterCounts`), and never reads
  the previous selection's summary.

## Amendment, 2026-11-03 — the summary is a read

- **`selection-summary` is gated at read level** (`.AsLedgerRead()`, as the other compute
  POSTs are). It reads rows any grant can already list, so a viewer's footer shows the
  count and the Σ (ADR-0083). The bulk writes in the same group stay write-gated, and a
  viewer is refused them with 422 `ledger-not-writable`.
- **A failed summary draws no Σ.** The footer names the failure (`useSelection`'s
  `summaryError`) in its trailing slot instead of a figure it does not have: the previous
  selection's Σ, or 0.

## Amendment, 2026-11-13 — the Σ is in one currency (ADR-0114 D7, D9)

- **`selection-summary` adds `currencyCode`**, the currency `sumOnAccount` is in, and
  `rateUnavailable` when the sum needs a missing rate; `sumOnAccount` is then null.
- **A real account's Σ is native**, in the account's own currency, as before.
- **A category's register sums each leg in the currency its row shows**: the real account
  on the other side of its posting, or the category's own between two categories. When
  the selected legs are in one currency the Σ is in it. When they are in several, each leg
  converts at its transaction's date into the ledger's reporting currency (a day before
  the server's UTC today at that day's rate, today or later at the latest), summed and
  rounded once at 4dp, as the register's Balance converts. A missing rate makes the Σ null
  and names each pair and day: never a partial sum, and never a sum across currencies.
- **The footer prints the Σ in `currencyCode`**, and "no rate", with the pairs on hover,
  in its place when it is null.
