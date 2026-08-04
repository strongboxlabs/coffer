import { useCallback, useEffect, useMemo, useRef } from 'react';
import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';

import {
    ApiError,
    fetchBalancesForHeaders,
    fetchIndexBuckets,
    fetchStatusCounts,
    setReconStatus,
} from '@/lib/api';
import type { ReconStatus, RegisterRow } from '@/lib/types';
import {
    useWindowedRegister,
    type UseWindowedRegisterResult,
} from '@/lib/useWindowedRegister';
import type { IndexBucketDto } from '@/lib/types/register';
import type {
    RegisterFilterArgs,
    RegisterServerStatus,
    RegisterStatusCounts,
} from '@/lib/api/register';
import type { StatusFilter } from './registerStatus';

/** Local calendar date as YYYY-MM-DD — used for the server-side "scheduled"
 *  status (posted after the user's local today, not the server's UTC one). */
function localToday(): string {
    const d = new Date();
    const p = (n: number) => n.toString().padStart(2, '0');
    return `${d.getFullYear().toString().padStart(4, '0')}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/**
 * Shared register-container controller (review #18). Extracted
 * verbatim from the duplicated wiring that BankRegisterPage and
 * InvestmentRegisterPage previously each reimplemented:
 *
 *   * the windowed-register hook itself,
 *   * the month-bucket query for the date-aware scroll-track,
 *   * `currentYearMonth` (topmost-entry-derived),
 *   * `seekToBucket` (re-seed the window on a bucket anchor),
 *   * `refreshLoadedBalances` (in-place balance refresh after a
 *     balance-affecting save / delete — no virtuoso data-swap),
 *   * the optimistic recon-status patch + mutation + cycle order.
 *
 * Both pages consume this so they differ only in row rendering +
 * editor wiring. This is an EXTRACTION, not a rewrite — the logic
 * is the same code, just moved here once.
 */

/**
 * Decide whether a saved edit moved a row's date-sorted position. The
 * register sorts by posted DATE (calendar day), so a same-day time
 * change — or no change — leaves the row where it is. Pure + exported
 * so the decision is unit-testable without the windowed-register hook.
 */
export function shouldRepositionForDate(
    oldPostedAt: string | undefined,
    newPostedAt: string,
): boolean {
    if (oldPostedAt === undefined) return false;
    return oldPostedAt.slice(0, 10) !== newPostedAt.slice(0, 10);
}

/**
 * Invalidate ONLY the per-account status-counts (the Show-dropdown badges), not
 * the scroll-rail buckets. Call after a mutation that shifts rows between status
 * views but leaves month totals untouched — a recon-status change (single or
 * bulk) or a needs_review/Approve edit. Mutations that add / remove / relocate
 * entries instead invalidate the whole
 * <c>['register-index-buckets', ledgerId, accountId]</c> parent, which already
 * covers counts (they're keyed under it — see the counts query below). Shared so
 * every optimistic status path refreshes counts the same way.
 */
export function invalidateRegisterStatusCounts(
    queryClient: QueryClient,
    ledgerId: string,
    accountId: string,
): void {
    void queryClient.invalidateQueries({
        queryKey: ['register-index-buckets', ledgerId, accountId, 'status-counts'],
    });
}

/**
 * The controller reloads the row window when the ADR-0079 sentinel query
 * (`['register', ledgerId, accountId]`) refetches — i.e. when a wholesale writer
 * invalidates the canonical key. React Query's `dataUpdatedAt` bumps on every
 * settle, so we compare against the last-seen value: skip `0` (not settled yet)
 * and skip the very first settle (the window's own initial load already has fresh
 * rows), then refresh on any later change. Pure so the skip-initial logic is
 * unit-tested without rendering.
 */
export function shouldRefreshOnSignal(
    lastSeen: number | null,
    updatedAt: number,
): { refresh: boolean; nextSeen: number | null } {
    if (updatedAt === 0) return { refresh: false, nextSeen: lastSeen };
    if (lastSeen === null) return { refresh: false, nextSeen: updatedAt };
    if (updatedAt !== lastSeen) return { refresh: true, nextSeen: updatedAt };
    return { refresh: false, nextSeen: lastSeen };
}

export interface UseRegisterControllerArgs {
    ledgerId: string;
    accountId: string;
    /** Optional `?focus=<headerId>` anchor for the initial window
     *  load (the Show-Other-Side arrival path). The investment page
     *  passes nothing; the bank page passes the URL focus param. */
    focusHeaderId?: string;
    pageSize: number;
    /** The active status filter. The controller derives the server-side hidden
     *  fetch from it (the Hidden tab walks soft-hidden rows, ADR-0072 D1), so
     *  that derivation lives HERE once instead of being re-derived per page.
     *  cleared/uncleared/scheduled/needs_review are also pushed server-side
     *  (folded into the filter) — no client-side status narrowing. */
    statusFilter: StatusFilter;
    /** The structured/search filter (mig 164): search + date/amount/security/
     *  tag/category. Status is derived from {@link statusFilter}, not this.
     *  MUST be a stable reference (the page memoizes it) — it feeds the
     *  windowed hook's effect deps. */
    filter?: RegisterFilterArgs;
    /** Column sort (mig 166). Display-order only — threaded to the windowed
     *  hook; the scroll-rail buckets + status counts are order-independent, so
     *  it doesn't touch them. Stable reference expected. */
    sort?: { column: string; dir: 'asc' | 'desc' };
}

export interface UseRegisterControllerResult {
    register: UseWindowedRegisterResult;
    indexBuckets: readonly IndexBucketDto[];
    /** Per-status entry counts for the status dropdown badges (mig 165);
     *  null until the first query resolves. */
    statusCounts: RegisterStatusCounts | null;
    /** `yyyy-MM` of the topmost loaded entry; null when empty. */
    currentYearMonth: string | null;
    /** Re-seed the window anchored at a bucket's `sampleHeaderId`. */
    seekToBucket: (sampleHeaderId: string) => void;
    /** Fetch fresh balance_after + net_amount for every loaded
     *  header and patch each entry in place. */
    refreshLoadedBalances: () => Promise<void>;
    /** After a saved edit, if the header's posted DATE changed, re-seed
     *  the window anchored at it so the row relocates to its new
     *  date-sorted position; returns true. Same-day (or unchanged) →
     *  no-op, returns false so the caller patches the row in place.
     *  The register sorts by posted date, so an in-place patch leaves a
     *  date-edited row stuck at its old slot — this moves it. */
    repositionIfDateChanged: (headerId: string, newPostedAt: string) => boolean;
    /** Optimistic recon-status patch over a set of header ids. */
    patchHeadersStatus: (
        headerIds: ReadonlySet<string>,
        status: ReconStatus,
    ) => void;
    reconStatusMutation: ReturnType<
        typeof useMutation<void, ApiError, { headerId: string; status: ReconStatus }>
    >;
    /** Cycle one header's recon status uncleared → reconciling →
     *  cleared → uncleared (fires the mutation). */
    cycleReconStatus: (headerId: string, current: ReconStatus) => void;
}

export function useRegisterController({
    ledgerId,
    accountId,
    focusHeaderId,
    pageSize,
    statusFilter,
    filter,
    sort,
}: UseRegisterControllerArgs): UseRegisterControllerResult {
    const queryClient = useQueryClient();

    // The Hidden tab re-fetches the soft-hidden payload (ADR-0072 D1); every
    // other filter walks the visible register. Derived HERE so both registers
    // get identical fetch behavior from one rule — not a per-page derivation
    // (which is how the investment Hidden view silently never fetched).
    const hidden = statusFilter === 'hidden';

    // Fold the status tabs into the server filter (mig 164): all/hidden carry no
    // server status (hidden uses the flag above); the rest push server-side so
    // the keyset cursor walks only matching entries — no client narrowing that
    // only sees the loaded window. Memoized so the windowed hook's effect dep
    // is a stable reference (resets the window only when the filter changes).
    const effectiveFilter = useMemo<RegisterFilterArgs>(() => {
        const serverStatus: RegisterServerStatus | undefined =
            statusFilter === 'all' || statusFilter === 'hidden'
                ? undefined
                : statusFilter;
        return {
            ...(filter ?? {}),
            status: serverStatus,
            // Only meaningful for status=scheduled/cleared/uncleared; harmless
            // otherwise.
            today: serverStatus ? localToday() : undefined,
        };
    }, [filter, statusFilter]);

    // Stable string key for cache-keying the buckets query per filter.
    const filterKey = useMemo(
        () => JSON.stringify(effectiveFilter),
        [effectiveFilter],
    );

    // Sliding-window register state. Initial load anchors on
    // `focusHeaderId` when present (the Show-Other-Side arrival
    // path); otherwise returns the most-recent K entries.
    const register = useWindowedRegister({
        ledgerId,
        accountId,
        focusHeaderId,
        pageSize,
        hidden,
        filter: effectiveFilter,
        sort,
    });

    // Month buckets for the date-aware scroll-track (ADR-0024
    // follow-up). One row per month-with-activity on this account,
    // most-recent first; invalidated by the mutation paths that
    // already call `register.refresh()`.
    const indexBucketsQuery = useQuery({
        queryKey: ['register-index-buckets', ledgerId, accountId, hidden, filterKey],
        queryFn: () => fetchIndexBuckets(ledgerId, accountId, { hidden, filter: effectiveFilter }),
        staleTime: 60_000,
    });
    const indexBuckets = indexBucketsQuery.data ?? [];

    // Per-status counts for the dropdown badges (mig 165). The endpoint buckets
    // across every status itself, so this uses the NON-status filter (status +
    // today only): keyed separately from the register/buckets so switching the
    // status view doesn't refetch counts (they don't change with the view).
    const countsFilter = useMemo<RegisterFilterArgs>(
        () => ({ ...(filter ?? {}), status: undefined, today: localToday() }),
        [filter],
    );
    const countsKey = useMemo(() => JSON.stringify(countsFilter), [countsFilter]);
    // Counts are keyed UNDER the buckets' per-account parent
    // (['register-index-buckets', ledgerId, accountId, …]) on purpose: the
    // scroll-rail buckets and these status counts are both per-account register
    // aggregates that change together on any create / delete / edit / move /
    // hide / merge, so every existing bucket-invalidation —
    // invalidateQueries(['register-index-buckets', ledgerId, accountId]) —
    // refreshes counts too. One namespace, no per-mutation dual-invalidation to
    // drift. Recon-status changes (which leave month totals untouched) invalidate
    // this 'status-counts' sub-key directly (see reconStatusMutation below + the
    // pages' bulk-recon path).
    const statusCountsQuery = useQuery({
        queryKey: ['register-index-buckets', ledgerId, accountId, 'status-counts', countsKey],
        queryFn: () => fetchStatusCounts(ledgerId, accountId, { filter: countsFilter }),
        staleTime: 30_000,
    });
    const statusCounts = statusCountsQuery.data ?? null;

    // Register-refresh contract (ADR-0079). The transaction ROWS are a bespoke
    // window (useWindowedRegister), not a TanStack query, so `invalidateQueries`
    // can't reach them. This sentinel IS a TanStack query keyed on the canonical
    // `['register', ledgerId, accountId]`: any WHOLESALE / EXTERNAL writer that
    // invalidates that key (or the `['register', ledgerId]` prefix) — via the
    // registerInvalidation helpers today, and the ADR-0012 SSE push handler
    // later — forces this query to refetch, which we turn into `register.refresh()`.
    // It carries no data and never hits the network; it's purely an invalidation
    // antenna. Local in-register edits do NOT touch this key (they patch the
    // window in place), so they never trigger a reload / scroll jump.
    const changeSignal = useQuery({
        queryKey: ['register', ledgerId, accountId],
        queryFn: () => null,
        staleTime: Infinity,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
    });
    // Skip the sentinel's own first settle (the window's initial load already has
    // fresh rows); every later `dataUpdatedAt` bump is an external invalidation →
    // reload the window (re-seeded at the top, where synced / imported / pushed
    // entries land).
    const lastSignalAt = useRef<number | null>(null);
    const refreshWindow = register.refresh;
    useEffect(() => {
        const { refresh, nextSeen } = shouldRefreshOnSignal(
            lastSignalAt.current,
            changeSignal.dataUpdatedAt,
        );
        lastSignalAt.current = nextSeen;
        if (refresh) refreshWindow();
    }, [changeSignal.dataUpdatedAt, refreshWindow]);

    // The current "you are here" month for the scroll-track. Derived
    // from the topmost loaded entry (entries[0] = newest in window).
    const currentYearMonth = useMemo(() => {
        const top = register.entries[0];
        const postedAt = top?.kind === 'group'
            ? top.legs[0]?.postedAt
            : top?.txn?.postedAt;
        if (!postedAt) return null;
        return postedAt.slice(0, 7); // "2026-05-21T..." → "2026-05"
    }, [register.entries]);

    // Seek handler shared by scroll-track + date-jump popover. Both
    // call `register.refresh(sampleHeaderId)`, which re-seeds the
    // windowed register anchored on that entry.
    const seekToBucket = useCallback(
        (sampleHeaderId: string) => {
            register.refresh(sampleHeaderId);
        },
        [register],
    );

    // In-place balance refresh: after a save / delete that shifts
    // downstream balances, fetch fresh `balance_after` + `net_amount`
    // for every header currently loaded, then patch each entry via
    // `mutateEntries`. No data swap, no virtuoso re-render, no
    // scroll jump.
    const refreshLoadedBalances = useCallback(async () => {
        const headerIds: string[] = [];
        for (const entry of register.entries) {
            const id = entry.kind === 'txn'
                ? entry.txn.headerId
                : entry.legs[0]?.headerId;
            if (id) headerIds.push(id);
        }
        if (headerIds.length === 0) return;
        const balances = await fetchBalancesForHeaders(ledgerId, accountId, headerIds);
        const byId = new Map(balances.map((b) => [b.headerId, b]));
        register.mutateEntries((entry) => {
            const id = entry.kind === 'txn'
                ? entry.txn.headerId
                : entry.legs[0]?.headerId;
            if (!id) return entry;
            const fresh = byId.get(id);
            if (!fresh) return entry;
            if (entry.kind === 'txn') {
                return {
                    ...entry,
                    txn: {
                        ...entry.txn,
                        balanceAfter: fresh.balanceAfter,
                        headerAccountNetAmount: fresh.netAmount,
                    },
                };
            }
            return {
                ...entry,
                legs: entry.legs.map((leg) => ({
                    ...leg,
                    balanceAfter: fresh.balanceAfter,
                    headerAccountNetAmount: fresh.netAmount,
                })),
            };
        });
    }, [register, ledgerId, accountId]);

    // Date-edit reposition. The register is sorted by posted date, so
    // patching a date-edited row in place leaves it stuck at its old
    // slot. When the saved date differs from the row's loaded date,
    // re-seed the window anchored at the header (same mechanism a new
    // transaction uses) so the row appears at its new position with the
    // scroll following it — what the user expects after a date edit.
    const repositionIfDateChanged = useCallback(
        (headerId: string, newPostedAt: string): boolean => {
            const current = register.entries.find((e) => {
                const id = e.kind === 'txn' ? e.txn?.headerId : e.legs?.[0]?.headerId;
                return id === headerId;
            });
            const oldPostedAt = current === undefined
                ? undefined
                : current.kind === 'txn'
                    ? current.txn?.postedAt
                    : current.legs?.[0]?.postedAt;
            if (!shouldRepositionForDate(oldPostedAt, newPostedAt)) return false;
            register.refresh(headerId);
            return true;
        },
        [register],
    );

    // Optimistic recon-status patch — applied synchronously so the
    // badge flips before the PUT round-trip completes. With the
    // sliding-window hook owning `entries`, there's no query-cache
    // snapshot to restore on error; we trust the mutation and rely
    // on a later refresh to surface server rejections. `cleared_at`
    // is set / cleared in lockstep so the optimistic row satisfies
    // the DB CHECK pattern client-side too.
    const patchHeadersStatus = useCallback(
        (headerIds: ReadonlySet<string>, status: ReconStatus) => {
            if (headerIds.size === 0) return;
            const optimisticClearedAt =
                status === 'cleared' ? new Date().toISOString() : null;
            const patchTxn = <R extends RegisterRow>(txn: R): R =>
                headerIds.has(txn.headerId)
                    ? {
                          ...txn,
                          status,
                          clearedAt: optimisticClearedAt,
                          clearedByUserId:
                              status === 'cleared' ? txn.clearedByUserId : null,
                      }
                    : txn;
            register.mutateEntries((entry) => {
                if (entry.kind === 'txn' && entry.txn) {
                    return { ...entry, txn: patchTxn(entry.txn) };
                }
                if (entry.kind === 'group' && entry.legs) {
                    return { ...entry, legs: entry.legs.map(patchTxn) };
                }
                return entry;
            });
        },
        [register],
    );

    const reconStatusMutation = useMutation<
        void,
        ApiError,
        { headerId: string; status: ReconStatus }
    >({
        mutationFn: (args) =>
            setReconStatus(ledgerId, args.headerId, {
                status: args.status,
                accountId,
            }),
        onMutate: (args) => {
            patchHeadersStatus(new Set([args.headerId]), args.status);
        },
        // A recon-status change moves the row between status views, so the
        // dropdown counts shift even though the badge flips optimistically in
        // place. Month totals are unchanged, so refresh only the counts.
        onSettled: () => {
            invalidateRegisterStatusCounts(queryClient, ledgerId, accountId);
        },
    });

    const cycleReconStatus = useCallback(
        (headerId: string, current: ReconStatus) => {
            const next: ReconStatus =
                current === 'uncleared'
                    ? 'reconciling'
                    : current === 'reconciling'
                      ? 'cleared'
                      : 'uncleared';
            reconStatusMutation.mutate({ headerId, status: next });
        },
        [reconStatusMutation],
    );

    return {
        register,
        indexBuckets,
        statusCounts,
        currentYearMonth,
        seekToBucket,
        refreshLoadedBalances,
        repositionIfDateChanged,
        patchHeadersStatus,
        reconStatusMutation,
        cycleReconStatus,
    };
}
