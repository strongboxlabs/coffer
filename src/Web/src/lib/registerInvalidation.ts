import type { QueryClient } from '@tanstack/react-query';

/**
 * The register-refresh contract (ADR-0079).
 *
 * A register's transaction ROWS are a bespoke sliding window
 * ({@link useWindowedRegister}), NOT a TanStack query — so ordinary
 * `invalidateQueries` can't reach them. To keep every writer uniform,
 * `useRegisterController` subscribes to the canonical key
 * `['register', ledgerId, accountId]`: invalidating it (or the
 * `['register', ledgerId]` prefix) reloads the mounted register (rows re-seeded
 * at the top, where new entries land) exactly like every other query refreshes
 * on invalidation.
 *
 * Call this from any WHOLESALE / EXTERNAL writer — a feed sync, a fired
 * reminder, a snapshot restore, a balance heal, a tag/category or account
 * rename — i.e. anything where this tab doesn't already know precisely which
 * rows changed. It invalidates the canonical key PLUS the sibling register
 * queries (scroll-rail buckets + status counts, account balances / review-dots,
 * holdings, the sidebar's account folders) AND drops the investment editor's
 * per-header ['header-legs'] draft-seed caches, so one call refreshes the whole
 * register surface and the rail beside it. It also refreshes the reads that carry
 * a currency taken from the accounts (ADR-0109 D9), since an account writer
 * reaches them through this seam: Structure's Categories tab (the ledger's display
 * currency, and whether its accounts span currencies), a security's page (its
 * hero, each trade's brokerage currency, and its price list, since a brokerage's
 * currency edit re-derives its trade prices in the new currency, ADR-0084 D2),
 * the securities catalog, and a tag's and a budget category's transaction lists,
 * each line in its own account's currency.
 *
 * Do NOT call this from a precise in-register edit (patch / delete / create /
 * recon-status / file import). Those already patch the loaded window
 * optimistically in place (`mutateEntries` / `removeEntries` / `refresh(anchor)`);
 * routing them through here would re-seed the window to the top and yank the user
 * off their row. They call {@link invalidateHomeReads} instead, beside their own
 * accounts refresh, so the home screen does not keep showing what they changed.
 *
 * When the ADR-0012 SSE pipeline lands (see docs/maintainer/follow-ups.md), its `txn-*`
 * push handler calls this on the same key — so server-originated changes (MCP
 * writes, other tabs, other users, background syncs) refresh a mounted register
 * through this identical seam, with no new wiring.
 */
export function invalidateLedgerRegister(
    queryClient: QueryClient,
    ledgerId: string,
): void {
    void queryClient.invalidateQueries({ queryKey: ['register', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['register-index-buckets', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['holdings', ledgerId] });
    // The sidebar's folders (FU-006) are ledger data a wholesale writer can
    // replace: a snapshot restore brings back the snapshot's folders, and a
    // rail still drawing the old tree would offer moves into folders that no
    // longer exist.
    void queryClient.invalidateQueries({ queryKey: ['account-folders', ledgerId] });
    // Reads whose currency comes from the accounts: a new account in another
    // currency, or an account's currency changed, moves them. A brokerage's
    // currency edit, and the trade-price repair, also rewrite its trade prices'
    // currency, which a security's price list prints.
    void queryClient.invalidateQueries({ queryKey: ['categories', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['security', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['security-transactions', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['security-prices', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['securities', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['tag-transactions', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['budget-transactions', ledgerId] });
    invalidateHomeReads(queryClient, ledgerId);
    // The investment editor SEEDS its draft from the ['header-legs', ledgerId,
    // headerId] cache and captures it once (useInvestmentTxnDraft), so a stale
    // seed can't self-correct on reopen. A wholesale / external writer reaching
    // this seam may have reshaped a header's legs, so drop the per-header seed
    // caches — the next editor open re-fetches. (An in-editor save already drops
    // its own header's entry in InvestmentTxnRowEdit.invalidateAfterSave; this
    // covers everything else — MCP writes, other tabs, category/tag renames.)
    void queryClient.removeQueries({ queryKey: ['header-legs', ledgerId] });
}

/**
 * The home screen's reads, and nothing of the register's (ADR-0079 as amended
 * 2026-11-03).
 *
 * Every figure on the home screen reads ledger data a write can move: its own
 * reads under ['home', ledgerId] (the review rows, recent transactions), the
 * overview's balances, and this month's spending. A precise writer — one that
 * patches the loaded register window in place — calls this beside its own
 * ['accounts', ledgerId] refresh, so the home screen is current when the user
 * goes back to it. It never touches ['register', …]: that would re-seed the
 * window and move the user off their row.
 *
 * The sidebar's section and folder totals are the overview's balances summed by the
 * server (ADR-0114 D7), so they refresh here too. They live under the folders' key,
 * not the accounts', so a folder write refreshes them as well; every write that
 * refreshes the accounts reaches them through this call (HomeReadsRule).
 */
export function invalidateHomeReads(
    queryClient: QueryClient,
    ledgerId: string,
): void {
    void queryClient.invalidateQueries({ queryKey: ['home', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['overview', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['budget-progress', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['account-folders', ledgerId, 'subtotals'] });
}

/**
 * The reads that show a converted figure (ADR-0114 D7, D9), for a write that moves the
 * rates they convert at or the currency they convert into: a hand-entered rate, a
 * change of the ledger's reporting currency. Every combined figure can move (the
 * overview, budget, categories, reports, the returns series, a category register's
 * balance), and so can an account whose holdings are priced in another currency, so
 * this is the wholesale refresh, plus the reads that seam does not reach: the ledger
 * list (its reporting currency), the rates view, the report reads, and the benchmark
 * list, whose availability names the series currencies a benchmark has no rate into
 * (ADR-0114 D1). A refresh that fetches rates and benchmark history is such a write too.
 */
export function invalidateConvertedReads(
    queryClient: QueryClient,
    ledgerId: string,
): void {
    invalidateLedgerRegister(queryClient, ledgerId);
    void queryClient.invalidateQueries({ queryKey: ['ledgers'] });
    void queryClient.invalidateQueries({ queryKey: ['fx-rates', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['benchmarks', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['report-summary', ledgerId] });
    void queryClient.invalidateQueries({ queryKey: ['returns-series', ledgerId] });
}
