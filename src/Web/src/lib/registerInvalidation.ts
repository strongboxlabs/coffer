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
 * holdings) AND drops the investment editor's per-header ['header-legs'] draft-
 * seed caches, so one call refreshes the whole register surface.
 *
 * Do NOT call this from a precise in-register edit (patch / delete / create /
 * recon-status / file import). Those already patch the loaded window
 * optimistically in place (`mutateEntries` / `removeEntries` / `refresh(anchor)`);
 * routing them through here would re-seed the window to the top and yank the user
 * off their row.
 *
 * When the ADR-0012 SSE pipeline lands (see docs/follow-ups.md), its `txn-*`
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
    // The investment editor SEEDS its draft from the ['header-legs', ledgerId,
    // headerId] cache and captures it once (useInvestmentTxnDraft), so a stale
    // seed can't self-correct on reopen. A wholesale / external writer reaching
    // this seam may have reshaped a header's legs, so drop the per-header seed
    // caches — the next editor open re-fetches. (An in-editor save already drops
    // its own header's entry in InvestmentTxnRowEdit.invalidateAfterSave; this
    // covers everything else — MCP writes, other tabs, category/tag renames.)
    void queryClient.removeQueries({ queryKey: ['header-legs', ledgerId] });
}
