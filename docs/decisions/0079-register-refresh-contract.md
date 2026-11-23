# 0079 — Register refresh: the canonical `['register', …]` invalidation key

* Status: Accepted; amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (the Categories tab's currency is the reporting currency)
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
stays mounted leaves it stale. Concretely:

- The sidebar **Sync all** runs a blanket `queryClient.invalidateQueries()` and
  *still* doesn't refresh the rows — the clearest proof the rows are unreachable
  by invalidation.
- Four writers already invalidate a `['register', …]` key (`ImportFileDialog`,
  `TagsPanel`, `CategoriesPanel`, and `GeneralPanel`'s balance-heal) — **a key
  nothing reads**: the contract their authors assumed existed.
- Settings sync (per-connection + Sync all), reminder-fire, snapshot restore,
  and opening-balance edits refreshed a register only on remount.
- `docs/maintainer/follow-ups.md` FU-035 (the SSE item, Phase 5+) already planned to *"invalidate the
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
  *(Amended 2026-10-27, ADR-0108 D14: a bank or investment PATCH, an approve
  and a merge answer `{ entries }`, every entry the saved header has in the
  scoped register (one on the side the header belongs to, one per line on an
  opposite side), and the SPA replaces that header's rows with exactly these.
  An edit, accept or merge that moves the row's sort key re-seats the window
  anchored at the row instead, so it lands at its sorted position with the
  server's running balances (`reseatIfMoved`); a write that keeps the key is
  patched in place as before.)*
  *(Amended 2026-11-03: a precise edit refreshes the home screen's reads
  without re-seeding the window. `invalidateHomeReads(qc, ledgerId)`
  invalidates `['home', ledgerId]`, `['overview', ledgerId]` and
  `['budget-progress', ledgerId]` and never `['register', …]`;
  `invalidateLedgerRegister` calls it, and every precise writer that
  refreshes `['accounts', ledgerId]` calls it beside that refresh: both
  registers' saves, accepts, merges, creates, deletes, bulk actions, imports
  and syncs, the investment editor's save, a security's edit, a security
  price's add, edit and delete, the catalog's price refresh, an account's
  deactivation and reactivation in the sidebar, the category settings and the
  feed account bindings. The tag writers (the Tags tab and a tag's own page)
  call it too, beside their `['register', ledgerId]` refresh: the home screen's
  recent and to-review rows draw each row's tags. Before this, only wholesale writers
  refreshed the home screen, so going back to it after an edit in a register
  showed the figures from before the edit.)*
  *(Amended 2026-11-04: `invalidateLedgerRegister` also invalidates the reads
  that carry a currency taken from the accounts (ADR-0109 D9):
  `['categories', ledgerId]` (Structure's Categories tab: the ledger's display
  currency and whether its accounts span currencies; since 2026-11-13 the reporting
  currency its totals convert into, ADR-0114), `['security', ledgerId]`
  and `['security-transactions', ledgerId]` (a security's hero and each trade's
  brokerage currency), `['security-prices', ledgerId]` (a security's price list:
  a brokerage's currency edit re-derives its trade prices in the new currency,
  ADR-0084 D2, and the trade-price repair rewrites them), `['securities',
  ledgerId]` (the catalog), and `['tag-transactions', ledgerId]` and
  `['budget-transactions', ledgerId]` (a tag's and a budget category's lines,
  each in its own account's currency). The account editor's create and edit and
  the trade-price repair reach them only through this seam, so a new account in
  another currency, or an account's currency changed, shows on those pages at
  once: without the price list here, a security's hero, refetched, named the new
  currency while its price list, read less than its stale time before, still
  printed the same trade prices in the old one.)*
  *(Amended 2026-11-05: a web test, `lib/HomeReadsRule.test.ts`, holds the
  2026-11-03 rule. It parses the SPA's source and finds every call of
  `invalidateQueries`, `refetchQueries`, `resetQueries` or `removeQueries`
  whose key starts with `'accounts'`, read from a list written out or a const
  that holds one, in the file or a module it imports by name; the function the
  call is written in must call `invalidateHomeReads` or
  `invalidateLedgerRegister` in its own body. A refresh whose key it cannot
  read (no filter, a predicate, a key built at run time) fails as
  unrecognised. A file whose accounts refreshes may go without the home
  screen's is listed with how many and why, and the count must match; none is
  listed.)*

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

### Amendment (2026-09): the index space is internal, and the grid says so

`firstItemIndex` is seeded to 1000000 so pages can be prepended without the
index going negative. That number was reaching `aria-rowindex` unchanged, so a
screen reader was told the register's first row was **row 1000001** — and its
paired `aria-rowcount` was the LOADED entry count, so the full announcement
paired that index with the size of the loaded window, on an account whose
history can run far past it.
The investment register announced neither, so moving through it said nothing
about position at all.

The offset is now stripped at the one place it enters the row callback
(`toWindowIndex`), so `renderRow` receives a **window-local** index and both
registers emit `aria-rowindex` from it. `aria-rowcount` is a hardcoded `-1` on
the shared scroll surface — ARIA's "the total is not known", which is the
honest answer for a sliding window whose offset into the account is not
derivable client-side.

A real total IS obtainable (the scroll-rail buckets sum to it), but it is
rejected: pairing a true total with a window-local index would announce
"row 5 of N" for a row thousands deep into the account, which is a worse lie
than no total at all. Announcing a true ABSOLUTE index would need
the window's base tracked through every prepend and eviction, and it is
unknowable anyway on a `focusHeaderId` deep link — so the announcement would
degrade mid-session, which is worse than a consistent one.

`toWindowIndex` is exported and unit-tested as a pure function rather than
through the component, and that is deliberate: **under jsdom virtuoso emits
LOCAL indices** — `firstItemIndex` needs layout to take effect — so a
rendered-DOM assertion reads "1" and "2" whether or not the subtraction
happens. A DOM test here is green against the bug it is supposed to catch.

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
