# 0079 — Register refresh: the canonical `['register', …]` invalidation key

* Status: Accepted
* Date: 2026-07-17
* Relates to: [0012](0012-sse-and-plain-http-no-signalr.md) (SSE push), [0076](0076-register-filter-single-source-of-truth.md)

## Context

The register's transaction ROWS are served by a bespoke sliding-window hook
(`useWindowedRegister`) — **not** React Query. It holds its own `WindowState`
(entries, per-page cursors, `firstItemIndex`) so it can page bidirectionally,
evict far pages under a memory cap, anchor on a focus header, and apply
optimistic in-place edits, all while feeding `react-virtuoso`'s `firstItemIndex`
for stable scroll across prepends/evictions.

The cost of opting out of React Query: **no `invalidateQueries` — however broad
— can refresh a mounted register's rows.** Only `register.refresh()` (or a
remount) reloads them. So any writer that changes register data while a register
stays mounted leaves it stale. Concretely (writer audit, 2026-07-17):

- The sidebar **Sync all** ran a blanket `queryClient.invalidateQueries()` and
  *still* didn't refresh the rows — the clearest proof the rows are unreachable
  by invalidation.
- Four writers already invalidated a `['register', …]` key (`ImportFileDialog`,
  `TagsPanel`, `CategoriesPanel`, and `GeneralPanel`'s balance-heal) — **a key
  nothing read.** Developers kept reaching for exactly this contract, assuming
  it existed.
- Settings sync (per-connection + Sync all), reminder-fire, snapshot restore,
  and opening-balance edits refreshed a register only on remount.
- `docs/follow-ups.md`'s SSE item (Phase 5+) already planned to *"invalidate the
  register queries; TanStack handles refetch"* — a plan that would ship broken
  for the same reason (the rows aren't a query).

## Decision

Establish one canonical invalidation contract for register data:

- **`['register', ledgerId, accountId]`** (and the `['register', ledgerId]`
  prefix) means "this account's register changed."
- `useRegisterController` (shared by the bank + investment registers) subscribes
  via a **sentinel `useQuery`** on that key — a no-data, no-network query whose
  only job is to refetch on invalidation and turn that into a `register.refresh()`.
  The bespoke window stays; the sentinel is the antenna.
- **Wholesale / external writers** — feed sync (sidebar + settings), fired
  reminder, snapshot restore, balance heal, tag / category / account rename —
  invalidate the key through one helper, `invalidateLedgerRegister(qc, ledgerId)`,
  which also refreshes the sibling register queries (scroll-rail buckets +
  status counts, account balances / review-dots, holdings).
- **Precise in-register edits** (patch / delete / create / recon-status / file
  import) do NOT touch the key. They already patch the loaded window
  optimistically in place; routing them through the key would re-seed the window
  to the top and lose the user's scroll position.

## Consequences

**Positive**
- A mounted register refreshes from any writer, uniformly, through the ordinary
  `invalidateQueries` contract — the sidebar Sync-all bug and its whole class
  are fixed.
- The four pre-existing dead `['register', …]` invalidations become live for
  free.
- The seam is exactly what the ADR-0012 SSE pipeline needs: its `txn-*` push
  handler calls `invalidateLedgerRegister` on the same key, so server-originated
  changes (MCP writes, other tabs, other users, background syncs) refresh a
  mounted register with no new wiring. The follow-up's "TanStack handles
  refetch" step finally has something to handle it.

**Negative**
- Two register keys with distinct roles: `['register', …]` (reload the row
  window) and `['register-index-buckets', …]` (scroll rail + status counts).
  Documented in `lib/registerInvalidation.ts` and commented at the call sites so
  a local edit doesn't invalidate the former (which would jump the scroll).
- The rows are still outside the cache — the sentinel is a bridge, not
  membership. The `useInfiniteQuery` port stays a possible future cleanup, but
  it must preserve the virtuoso index math and the discard-and-reseed-on-
  head-insert behavior this contract relies on.

## Alternatives considered

- **Port `useWindowedRegister` to `useInfiniteQuery`.** The "textbook" shape,
  but React Query owns neither `react-virtuoso`'s `firstItemIndex` bookkeeping
  across a bounded bidirectional window (we'd hand-roll it anyway by diffing
  RQ's `pages`) nor a head-insertion-safe refetch — its refetch-loaded-pages-in-
  place model corrupts cursor page boundaries when new rows land at the head
  (the sync case), which `refresh()`'s discard-and-reseed already handles. We'd
  rewrite a tested hook and still hand-roll the hard parts. Deferred to its own
  slice if ever justified.
- **A feed-sync-specific signal.** Solves only the one symptom; import,
  reminder-fire, restore, and MCP-via-SSE would each re-solve it. Rejected as a
  one-off.
- **Tie the row refresh to the existing `['register-index-buckets', …]`
  refetch.** Local edits already invalidate that key, so it would re-seed to the
  top after every in-place edit — regressing scroll position. Rejected.
