import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from '@tanstack/react-router';
import { type VirtuosoHandle } from 'react-virtuoso';

import { Upload, RefreshCw } from 'lucide-react';

import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { MainArea } from '@/components/ui/SidebarLayout';
import {
    ApiError,
    bulkDeleteTransactions,
    bulkSetReconStatus,
    deleteInvestmentTransaction,
    deleteTransaction,
    fetchAccounts,
    fetchHeaderLegs,
    fetchSecurities,
    fetchTags,
    fetchVisibleLedgers,
    syncAccount,
} from '@/lib/api';
import { buildAccountPathMap } from '@/lib/accountPath';
import {
    buildDisplayRows,
    canonicalLeg,
    groupAmount,
    groupBalanceAfter,
    regroupTargetSplits,
    type DisplayRow,
    type RegisterEntryOf,
} from '@/lib/splitCollapse';
import { useSelection } from '@/lib/useSelection';
import type {
    AccountSummary,
    BulkDeleteResponse,
    BulkReconStatusResponse,
    DeleteTransactionResponse,
    LedgerSummary,
    ReconStatus,
    RegisterEntry,
} from '@/lib/types';

import { classifySimpleFinDescription } from '@/lib/ingest/simpleFinDescriptionClassifier';
import { formatLedgerDate } from '@/lib/dates';

import { PortfolioBar, HoldingsTable, type AccountView } from '../../HoldingsPanel';
import { ACTION_LAYOUTS } from '../../investment-edit/actionLayout';
import { hintToDraft } from '../../investment-edit/hintToDraft';
import { InvestmentTxnRowEdit } from '../../investment-edit/InvestmentTxnRowEdit';
import { legsToDraft } from '../../investment-edit/legsToDraft';
import type { InvestmentTxnDraft } from '../../investment-edit/validation';
import { ReminderEditorDialog } from '../../reminders/ReminderEditorDialog';
import { RegisterTopBar } from '../shell/RegisterTopBar';
import { RegisterShell } from '../shell/RegisterShell';
import { RegisterScrollSurface } from '../shell/RegisterScrollSurface';
import { ImportFileDialog } from '../shell/ImportFileDialog';
import {
    invalidateRegisterStatusCounts,
    useRegisterController,
} from '../shell/useRegisterController';
import { useRegisterKeyboardNav } from '../shell/useRegisterKeyboardNav';
import { RegisterDeleteConfirm, type PendingDelete } from '../shell/RegisterDeleteConfirm';
import { RegisterBulkActionBar } from '../shell/RegisterBulkActionBar';
import { useRegisterBulkRecovery } from '../shell/useRegisterBulkRecovery';
import { MoveToAccountDialog } from '../shell/MoveToAccountDialog';
import { RegisterScrollTrack } from '../RegisterScrollTrack';
import { RegisterDateJumpPopover } from '../RegisterDateJumpPopover';
import { RegisterControlsBar } from '../shell/RegisterControlsBar';
import { DEFAULT_SORT, type RegisterSortState } from '../shell/registerSort';
import { RegisterLeadHeaderCells } from '../shell/RegisterRowLead';
import { RegisterVirtualList } from '../shell/RegisterVirtualList';
import {
    passesStatusFilter,
    resolveRowStatus,
    toSelectionStatusFilter,
    type StatusFilter,
} from '../shell/registerStatus';
import { isRegisterFilterActive, type RegisterFilterArgs } from '@/lib/api/register';
import { INVESTMENT_REGISTER_COLS } from './columns';
import { RegisterRow } from '../shell/RegisterRow';
import { investmentRowStrategy } from '../strategies/investmentRowStrategy';

import type { LedgerInvestmentAction } from '@/lib/types';
import type { InvestmentRow as InvestmentRowType } from '@/lib/types';

const PAGE_SIZE = 100;

/**
 * Narrow a row's header action (`investmentAction`, typed `string |
 * null` on the wire) to a `LedgerInvestmentAction`. Keyed off
 * `ACTION_LAYOUTS` so the recognized set stays in lockstep with the
 * editor's action matrix (no hand-maintained list, no coercion). Used
 * by the Duplicate path to decide whether a row is an investment-shape
 * event that `legsToDraft` can invert.
 */
function isLedgerInvestmentAction(
    value: string | null,
): value is LedgerInvestmentAction {
    return value !== null && value in ACTION_LAYOUTS;
}

/**
 * Per-display-row focus identity for the investment register
 * (ADR-0036; ADR-0028 refinement 2026-06). Mirrors bank's
 * `displayRowId`, with one nuance the investment register keeps:
 *
 *   - flat `txn` rows: originating-side rows use `headerId` so focus
 *     survives an investment PATCH (ADR-0025 reshape preserves
 *     headerId but issues new leg ids); single target-split rows use
 *     `id` because multiple share a headerId.
 *   - `split-parent` rows: `g:<groupId>` (the collapsed cluster).
 *   - `split-leg` rows: the leg's own `id` (each leg is distinct).
 */
function displayRowId(row: DisplayRow<InvestmentRowType>): string {
    if (row.kind === 'txn') {
        const isTargetSplit =
            row.txn.accountPostingsOnHeader < row.txn.headerTotalPostings;
        return isTargetSplit ? row.txn.id : row.txn.headerId;
    }
    if (row.kind === 'split-parent') return `g:${row.groupId}`;
    return row.leg.id;
}

/**
 * Investment-domain register page (ADR-0030 §3 / roadmap A4.d).
 * Renders for accounts whose <c>accountType === 'investment'</c>;
 * <c>RegisterRouter</c> dispatches here based on the resolved
 * account.
 */
export function InvestmentRegisterPage() {
    const { ledgerId, accountId } = useParams({ strict: false }) as {
        ledgerId: string;
        accountId: string;
    };
    const navigate = useNavigate();

    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    // Full account universe (incl. inactive) — mirrors BankRegisterPage: the
    // register resolves any account (so an inactive brokerage's register still
    // shows its name + currency) and renders any counterparty. The editor's
    // field pickers filter to active themselves (isEligible below), so inactive
    // accounts aren't offered for new picks. Shared includeInactive cache key.
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
    });
    // Securities catalog — feeds the register filter's Security picker
    // (investment-only). Small, stable per-ledger list.
    const securitiesQuery = useQuery({
        queryKey: ['securities', ledgerId],
        queryFn: () => fetchSecurities(ledgerId),
    });
    // Ledger tag dictionary — feeds the Tag filter's autocomplete.
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
        staleTime: 60_000,
    });
    // Server-side register filter (mig 164): search + date/amount range +
    // category/tag/security. Owned here; the controller derives status/today
    // and threads it through the windowed fetch + index buckets.
    const [filter, setFilter] = useState<RegisterFilterArgs>({});

    const ledger = ledgersQuery.data?.find((l: LedgerSummary) => l.id === ledgerId) ?? null;
    const account = accountsQuery.data?.find((a: AccountSummary) => a.id === accountId) ?? null;
    const currency = account?.currencyCode ?? 'USD';
    // Account-id → full slash path, so the register's category / transfer
    // chips render their parent→child chain (ADR-0068 Slice A). Covers
    // every account incl. categories (categories ARE accounts).
    const accountPaths = useMemo(
        () => buildAccountPathMap(accountsQuery.data ?? []),
        [accountsQuery.data],
    );
    const isInvestment = account?.accountType === 'investment';

    // Two views share this page: the activity register (default) and the
    // Holdings positions table. Local state — Activity is the primary view,
    // Holdings is a quick peek; both keep the same PortfolioBar on top.
    const [view, setView] = useState<AccountView>('activity');

    const queryClient = useQueryClient();

    // Status filter (All / Cleared / Uncleared N / Scheduled N) — the
    // same shared tabs the bank register uses (ADR-0030 reuse). Applied
    // client-side over the aggregated (displayed) rows, and fed into
    // useSelection so a 'select-all' click selects only rows matching
    // the active filter — identical semantics to bank.
    const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
    // Column sort (mig 166). Threaded to the controller (windowed read only)
    // and the controls bar's Sort ▾ menu; an investment register also offers
    // the Security / Shares / Price / Action columns.
    const [sort, setSort] = useState<RegisterSortState>(DEFAULT_SORT);

    // Which target-split clusters are expanded (ADR-0028 refinement
    // 2026-06, mirroring bank's `expandedGroups`). Keyed by group id
    // (= the cluster's header id). A collapsed cluster renders one
    // split-parent row; expanding reveals its leg rows.
    const [expandedGroups, setExpandedGroups] = useState<ReadonlySet<string>>(
        () => new Set(),
    );
    const toggleGroupExpanded = useCallback((groupId: string) => {
        setExpandedGroups((prev) => {
            const next = new Set(prev);
            if (next.has(groupId)) next.delete(groupId);
            else next.add(groupId);
            return next;
        });
    }, []);

    // Pin "today" once per render so the scheduled/uncleared derivation
    // is stable for the duration of a render pass. Day-level resolution.
    const today = useMemo(() => new Date(), []);

    // Bulk-selection state (ADR-0024). Mirrors the bank register: the
    // hook owns the discriminated explicit/all selection + a debounced
    // server-side summary query (count, Σ). The hook is domain-agnostic
    // (operates on headerId + createdAt), so it's reused verbatim; the
    // active status filter is threaded in so 'all'-mode selections honor
    // the same predicate the visible tabs apply.
    const selection = useSelection({
        ledgerId,
        accountId,
        // The server-side selection filter mirrors every register tab,
        // including "Needs review" (resolved via txn_headers.needs_review),
        // so an 'all'-mode select-all honors exactly the active filter.
        statusFilter: toSelectionStatusFilter(statusFilter),
        // Non-status filter/search dims (mig 164) so an 'all'-mode select-all
        // covers exactly the filtered set, not the whole account.
        filter,
    });

    // Shared register-container controller (review #18 — same hook
    // the bank register uses; per ADR-0028 the windowing behavior is
    // shape-agnostic). Owns the windowed register, the index-bucket
    // query, currentYearMonth, seekToBucket, refreshLoadedBalances,
    // and the recon-status optimistic patch + mutation + cycle.
    const {
        register,
        indexBuckets,
        statusCounts,
        currentYearMonth,
        seekToBucket,
        refreshLoadedBalances,
        repositionIfDateChanged,
        patchHeadersStatus,
        cycleReconStatus,
    } = useRegisterController({
        ledgerId,
        accountId,
        pageSize: PAGE_SIZE,
        statusFilter,
        filter,
        sort,
    });

    // When any user filter is active the index buckets already reflect the
    // same filtered set, so their total IS the match count (null hides the
    // count chip). Mirrors bank.
    const filterActive = isRegisterFilterActive(filter);
    const filterResultCount = filterActive
        ? indexBuckets.reduce((sum, b) => sum + b.count, 0)
        : null;

    // Investment EVENTS are already collapsed server-side (ADR-0080), so
    // the only client-side entry transform left is clustering ADR-0036
    // target-split legs into expandable parents — the shared regroup the
    // bank register also uses. Memoized on the raw entries reference.
    const allEntries = useMemo(
        () => regroupTargetSplits(narrowToInvestmentEntries(register.entries)),
        [register.entries],
    );

    // Status is derived from an entry's canonical row — `entry.txn`
    // for a flat row, or the leg_index=0 leg (`legs[0]`) for a
    // re-grouped target-split cluster. Every leg of a cluster shares
    // one posted_at + feed_status, so the canonical leg is
    // representative (mirrors bank's `entryStatusRow`).
    const entryStatusRow = (
        entry: RegisterEntryOf<InvestmentRowType>,
    ): InvestmentRowType =>
        entry.kind === 'txn' ? entry.txn : entry.legs[0]!;

    // Status counts over the FULL aggregated list (pre-filter) — drives
    // the Uncleared / Scheduled count badges on the tabs, same as bank
    // computes its counts over `allEntries`. Reads the canonical row of
    // each entry so re-grouped target-split clusters count once.
    // The displayed list — the aggregated entries that pass the active
    // status filter. This is what the user sees + navigates + selects,
    // mirroring bank's `visibleEntries`. Re-grouped target-split
    // clusters (`kind:'group'`) pass through too, filtered by their
    // canonical leg's status (like bank filters a group via
    // `entryStatusRow`). Everything downstream (displayRows, keyboard
    // nav, select-all set) reads this so the filter, counts, and bulk
    // select-all stay consistent.
    const visibleEntries = useMemo(
        () =>
            allEntries.filter((entry) =>
                passesStatusFilter(entryStatusRow(entry), statusFilter, today),
            ),
        [allEntries, statusFilter, today],
    );


    // Translate the visible entry stream into a flat DisplayRow list:
    // flat `txn` rows pass through, target-split clusters emit a
    // collapsed `split-parent` row (+ `split-leg` rows when expanded).
    // Generic over `InvestmentRowType` so the row components
    // receive the narrowed type — same pipeline the bank register uses.
    const displayRows = useMemo(
        () => buildDisplayRows<InvestmentRowType>(visibleEntries, expandedGroups),
        [visibleEntries, expandedGroups],
    );

    // Header ids of the selectable rows in display order — powers shift-click
    // range selection (useSelection.extendSelectionTo). Split-leg rows share
    // their parent's header, so they contribute no new ids.
    const orderedHeaderIds = useMemo(() => {
        const ids: string[] = [];
        let last: string | null = null;
        for (const row of displayRows) {
            const id = row.kind === 'txn'
                ? row.txn.headerId
                : row.kind === 'split-parent'
                    ? (row.legs[0]?.headerId ?? null)
                    : null;
            if (id !== null && id !== last) { ids.push(id); last = id; }
        }
        return ids;
    }, [displayRows]);

    // Synthesized split-parent rows for the re-grouped target-split
    // clusters. The collapsed parent shows the cluster's NET amount + the
    // REAL post-header balance. Both come from the SHARED split helpers —
    // the same ones the bank register uses: `groupAmount` reads the
    // server-computed `headerAccountNetAmount` (not a client sum),
    // `groupBalanceAfter` is the highest-leg_index balance. The canonical
    // leg already carries the server-projected slots (transfer
    // counterparty etc.), so the aggregate is just that leg plus the
    // rolled-up amount/balance (ADR-0080). Memoized keyed by group id.
    const splitParentAggregates = useMemo(() => {
        const map = new Map<string, InvestmentRowType>();
        for (const entry of visibleEntries) {
            if (entry.kind !== 'group') continue;
            const legs = entry.legs;
            map.set(entry.groupId, {
                ...canonicalLeg(legs),
                amount: groupAmount(legs),
                balanceAfter: groupBalanceAfter(legs),
                legIndex: 0,
            });
        }
        return map;
    }, [visibleEntries]);

    const [editingHeaderId, setEditingHeaderId] = useState<string | null>(null);
    const [isCreatingNew, setIsCreatingNew] = useState(false);
    // Duplicate prefill (mirrors the bank register's Duplicate action):
    // when set, the new-transaction editor opens pre-seeded with a copy
    // of the right-clicked row's draft (built via `legsToDraft`, the
    // same inversion the edit path uses). It only pre-fills the form —
    // 'new' mode still POSTs a brand-new header. Cleared on cancel + on
    // a successful create so the next plain "+ New" starts blank.
    const [duplicateDraft, setDuplicateDraft] = useState<InvestmentTxnDraft | null>(null);

    // ADR-0051 slice C: "Create reminder" from a row opens the reminder editor
    // prefilled from the inverted draft (source = this brokerage), with the
    // schedule left blank. Same legsToDraft inversion as Duplicate.
    const [reminderFromInvestment, setReminderFromInvestment] =
        useState<{ sourceAccountId: string; draft: InvestmentTxnDraft } | null>(null);

    // virtuoso handle for scrollIntoView (keyboard nav) + the
    // viewport-center yearMonth for the scroll-track marker.
    const virtuosoRef = useRef<VirtuosoHandle | null>(null);
    const [scrollParent, setScrollParent] = useState<HTMLDivElement | null>(null);
    const [viewportYearMonth, setViewportYearMonth] = useState<string | null>(null);

    // Keyboard navigation + focus cursor (review #18 — shared with
    // the bank register). Iterates `displayRows` (post-aggregation +
    // status filter + split-collapse) so ArrowDown steps through the
    // exact collapsed row list the user sees. Focus id comes from
    // `displayRowId`: headerId on originating-side flat rows (survives
    // an investment PATCH reshape), leg id on single target rows,
    // `g:<id>` on split-parents, leg id on expanded leg rows. `N`
    // opens the new-transaction editor; `Enter` edits the focused
    // originating-side row only (target splits + split-parents are
    // read-only).
    const { focusedRowId, setFocusedRowId } = useRegisterKeyboardNav<DisplayRow<InvestmentRowType>>({
        rows: displayRows,
        getRowId: displayRowId,
        onLoadNewer: register.loadNewer,
        onLoadOlder: register.loadOlder,
        // Filtering is server-side (mig 164) — the payload IS the filtered
        // set, so edge-load always paginates through matches.
        suppressEdgeLoad: false,
        // Investment register feeds virtuoso plain (local) indices —
        // it doesn't thread firstItemIndex into the list — so the
        // scroll target is the local index directly.
        scrollRowIntoView: (localIndex) =>
            virtuosoRef.current?.scrollIntoView({ index: localIndex }),
        enabled: editingHeaderId === null && !isCreatingNew,
        onCreate: startCreate,
        onEnterRow: (currentId, e) => {
            const row = displayRows.find((r) => displayRowId(r) === currentId);
            // Only flat originating-side `txn` rows are editable. A
            // split-parent is a read-only target-split cluster, and a
            // single target row opens only on its originating account;
            // leave the default intact (no preventDefault) on those.
            if (row?.kind !== 'txn') return;
            const isTargetSplit =
                row.txn.accountPostingsOnHeader < row.txn.headerTotalPostings;
            if (isTargetSplit) return;
            e.preventDefault();
            startEdit(row.txn.headerId);
        },
    });
    const [contextMenu, setContextMenu] = useState<{
        anchor: { x: number; y: number };
        target: InvestmentRowType;
    } | null>(null);
    const [pendingDelete, setPendingDelete] =
        useState<PendingDelete<InvestmentRowType>>(null);
    // Right-click → "Show raw data" target. Diagnostic-only — pops a
    // modal with the row + its raw legs as JSON. Particularly useful
    // when debugging classifier coverage (you see the exact payee
    // text the orchestrator received).
    const [rawDataTarget, setRawDataTarget] = useState<InvestmentRowType | null>(null);
    // ADR-0080: the register window returns collapsed events, not raw legs,
    // so the diagnostic modal fetches the full leg set on demand from the
    // shared ['header-legs'] cache (warm when the editor has touched it).
    const rawDataLegsQuery = useQuery({
        queryKey: ['header-legs', ledgerId, rawDataTarget?.headerId ?? ''],
        queryFn: () => fetchHeaderLegs(ledgerId, rawDataTarget!.headerId),
        enabled: rawDataTarget !== null,
    });

    // File-import wizard (OFX/QFX/QIF — format by file extension).
    const [importDialogOpen, setImportDialogOpen] = useState(false);

    // Per-account SimpleFIN sync — parity with the bank register.
    // Only surfaced when this brokerage account is bound to a feed
    // connection; click pulls just this account's transactions.
    const syncAccountMutation = useMutation({
        mutationFn: () => syncAccount(ledgerId, accountId),
        onSuccess: () => {
            register.refresh();
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({
                queryKey: ['register-index-buckets', ledgerId, accountId],
            });
            queryClient.invalidateQueries({
                queryKey: ['holdings', ledgerId, accountId],
            });
        },
    });

    const closeContextMenu = useCallback(() => setContextMenu(null), []);

    function startCreate() {
        // Entering new-mode cancels any in-flight edit to keep the
        // surface mutually exclusive — same pattern as bank.
        setEditingHeaderId(null);
        setIsCreatingNew(true);
    }
    function startEdit(headerId: string) {
        setIsCreatingNew(false);
        setEditingHeaderId(headerId);
    }
    function cancelEdit() {
        setEditingHeaderId(null);
        setIsCreatingNew(false);
        // Drop any Duplicate seed so the next plain "+ New" starts
        // blank (cancel + post-create both route through here).
        setDuplicateDraft(null);
    }

    // Delete mutation: investment-specific endpoint. On success
    // refreshes the windowed register so every downstream row's
    // balance reflects the deletion. removeEntries alone would
    // drop the deleted row but leave the rest of the window
    // showing pre-delete balance values — same class of bug as
    // patch-without-refresh on balance-affecting saves.
    const [deleteError, setDeleteError] = useState<string | null>(null);
    const deleteMutation = useMutation<
        DeleteTransactionResponse,
        ApiError,
        InvestmentRowType
    >({
        // Investment-shape headers (action != null) go through the
        // dedicated /investment-transactions DELETE; bank-shape headers
        // landing in this register (cash sweeps, ACH credits feeding
        // the cash sleeve, transfer counter-legs) go through the
        // universal /transactions DELETE — the investment endpoint
        // 422s on `HeaderNotInvestment` and the user sees nothing.
        mutationFn: (target) =>
            target.investmentAction !== null
                ? deleteInvestmentTransaction(ledgerId, target.headerId)
                : deleteTransaction(ledgerId, target.headerId),
        onSuccess: (_response, target) => {
            // In-place delete: drop the row, then refresh downstream
            // balances in the loaded window. Avoids the full
            // register.refresh data-swap (and the scroll jerk).
            const deletedId = target.headerId;
            register.removeEntries((entry) =>
                (entry.kind === 'txn' ? entry.txn.headerId : entry.legs[0]!.headerId)
                    === deletedId);
            void refreshLoadedBalances();
            // Match the bank delete: a deleted lot shifts this account's cash
            // balance / review-dot count AND its holdings — invalidate both, not
            // just the scroll-rail buckets (prior asymmetry).
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['holdings', ledgerId, accountId] });
            queryClient.invalidateQueries({
                queryKey: ['register-index-buckets', ledgerId, accountId],
            });
        },
        onError: (err) => {
            setDeleteError(err.detail || err.message || 'Delete failed.');
        },
    });

    // Bulk recon-status mutation (ADR-0024). Identical wiring to the
    // bank register: one round-trip per status, server resolves the
    // predicate + applies the status in a single atomic UPDATE, and the
    // SPA optimistically flips the badge on every loaded header that's
    // in the active selection. The recon status is a universal header
    // field, so this stays enabled even when the selection includes
    // read-only target rows (only Delete is gated).
    const bulkReconStatusMutation = useMutation<
        BulkReconStatusResponse,
        ApiError,
        ReconStatus
    >({
        mutationFn: (status) =>
            bulkSetReconStatus(ledgerId, selection.selection, accountId, status),
        onMutate: (status) => {
            // Optimistic: walk currently-loaded entries, find every
            // header in the active selection, flip its status badge.
            const visibleHeaders = new Set<string>();
            for (const entry of register.entries) {
                const row = entry.kind === 'txn' ? entry.txn : entry.legs[0]!;
                if (selection.isSelected(row.headerId, row.createdAt)) {
                    visibleHeaders.add(row.headerId);
                }
            }
            patchHeadersStatus(visibleHeaders, status);
        },
        onSuccess: () => {
            // Clear selection once the bulk intent is fulfilled — same
            // posture as bank (no dangling N-selected state across a
            // now-stale predicate).
            selection.clear();
            // Bulk recon-status shifts rows between status views; refresh the
            // dropdown counts (month totals unchanged, so counts-only).
            invalidateRegisterStatusCounts(queryClient, ledgerId, accountId);
        },
    });

    // Bulk delete mutation (ADR-0024). Mirrors the bank register: the
    // server applies the per-row hard-delete vs soft-hide policy across
    // the whole selection in one transaction and recomputes balances.
    // We refresh the window so every remaining row surfaces its updated
    // balance, then clear the selection.
    const bulkDeleteMutation = useMutation<BulkDeleteResponse, ApiError, void>({
        mutationFn: () =>
            bulkDeleteTransactions(ledgerId, selection.selection),
        onSuccess: () => {
            selection.clear();
            register.refresh();
            // Bulk delete can remove `needs_review` rows; invalidate
            // the per-ledger accounts query so the sidebar's green
            // review-dot (sourced from `needsReviewCount`) resets live
            // without a page reload.
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({
                queryKey: ['register-index-buckets', ledgerId, accountId],
            });
            queryClient.invalidateQueries({
                queryKey: ['holdings', ledgerId, accountId],
            });
        },
        onError: (err) => {
            setDeleteError(err.detail || err.message || 'Bulk delete failed.');
        },
    });

    // Bulk recovery (Unhide / Move, ADR-0072 D2/D3) — shared with the bank
    // register via useRegisterBulkRecovery. Investment also invalidates
    // holdings after a bulk op.
    const recovery = useRegisterBulkRecovery({
        ledgerId,
        accountId,
        selection,
        onRefresh: () => register.refresh(),
        invalidateHoldings: true,
    });

    // Header select-all + per-row selection predicates run over the
    // VISIBLE display rows (the user-facing collapsed list). Each
    // carries its `createdAt` so the 'all'-mode predicate stays honest
    // (rows newer than the select-all click aren't part of the
    // selection — ADR-0024 #3). Dedup by headerId: split-leg rows share
    // a header with their parent (collected via the parent), and
    // selection is header-level, so a header contributes once.
    const visibleRowsForSelection = useMemo(() => {
        const seen = new Set<string>();
        const out: { headerId: string; createdAt: string }[] = [];
        for (const row of displayRows) {
            let headerId: string;
            let createdAt: string;
            if (row.kind === 'txn') {
                headerId = row.txn.headerId;
                createdAt = row.txn.createdAt;
            } else if (row.kind === 'split-parent') {
                headerId = row.legs[0]!.headerId;
                createdAt = row.legs[0]!.createdAt;
            } else {
                // split-leg rows share their group's header — already
                // collected via the parent above.
                continue;
            }
            if (seen.has(headerId)) continue;
            seen.add(headerId);
            out.push({ headerId, createdAt });
        }
        return out;
    }, [displayRows]);

    const allVisibleSelected = selection.isAllVisibleSelected(visibleRowsForSelection);
    const someVisibleSelected = selection.isSomeVisibleSelected(visibleRowsForSelection);
    const selectAllRef = useRef<HTMLInputElement>(null);
    useEffect(() => {
        if (selectAllRef.current) {
            selectAllRef.current.indeterminate = someVisibleSelected;
        }
    }, [someVisibleSelected]);

    // Footer count/Σ — defer to the server summary when present; fall
    // back to the explicit ids count while the debounced summary query
    // is still in flight (so the footer doesn't blink 0 on the first
    // checkbox click). Same fallback as bank.
    const selectedCount =
        selection.summary?.count ??
        (selection.selection.kind === 'explicit'
            ? selection.selection.headerIds.length
            : 0);
    const selectedSum = selection.summary?.sumOnAccount ?? 0;

    // Read-only target rows (ADR-0036): a row whose header puts fewer
    // postings on THIS account than the header has in total is the
    // counter-side of a multi-posting header; its canonical owner is
    // the originating account. Bulk Delete sends the selection to
    // `/transactions/bulk-delete`, so including a read-only target row
    // would either mutate a cross-domain header or 422 mid-batch.
    // Only meaningful for 'explicit' selections (returns false in
    // 'all'-mode): in 'all'-mode the server restricts bulk ops to
    // headers this account ORIGINATES (ADR-0036), so unloaded
    // target-split rows are excluded server-side — nothing for the
    // client to audit. Gate exactly as bank does.
    const selectionHasReadOnly = useMemo(() => {
        if (selection.selection.kind !== 'explicit') return false;
        const selectedIds = new Set(selection.selection.headerIds);
        for (const row of displayRows) {
            // A collapsed split-parent IS a read-only target-split
            // cluster — check its canonical leg. A flat `txn` row is
            // read-only when it's a single target split
            // (accountPostingsOnHeader < headerTotalPostings). split-leg
            // rows share their parent's header (covered by the parent).
            const probe =
                row.kind === 'txn'
                    ? row.txn
                    : row.kind === 'split-parent'
                        ? row.legs[0]!
                        : null;
            if (probe === null) continue;
            if (!selectedIds.has(probe.headerId)) continue;
            if (probe.accountPostingsOnHeader < probe.headerTotalPostings) {
                return true;
            }
        }
        return false;
    }, [selection.selection, displayRows]);

    // Disabled only when an explicit selection includes a read-only
    // row. 'all'-mode is now allowed: the server restricts all-mode
    // bulk ops to headers this account ORIGINATES (ADR-0036), so it can
    // never touch a header owned by another account. The typed-confirm
    // dialog still guards large all-mode deletes.
    const bulkDeleteDisabled = selectionHasReadOnly;
    const bulkDeleteDisabledTitle = selectionHasReadOnly
        ? 'Selection includes rows whose canonical owner is elsewhere (split counter-side). Delete those from the source register.'
        : 'Delete the active selection';

    function buildRowMenuItems(target: InvestmentRowType): ContextMenuItem[] {
        // ADR-0036: target-side rows (counter of a multi-posting
        // header) are read-only on this register; the editor opens
        // on the originating account. Hide Edit + Delete; keep
        // "Show raw data" for inspection and add "Show other side"
        // so the user can jump to the originating account to edit
        // (parity with bank register's same affordance).
        const isTargetSplit =
            target.accountPostingsOnHeader < target.headerTotalPostings;
        const items: ContextMenuItem[] = [];
        if (!isTargetSplit) {
            items.push({
                id: 'edit',
                label: 'Edit',
                onSelect: () => startEdit(target.headerId),
                shortcutHint: 'Enter',
            });
            // Duplicate (parity with the bank register): open the
            // new-transaction editor pre-seeded with a copy of this
            // row's draft. We invert the source legs with the SAME
            // `legsToDraft` the edit path uses (full leg set keyed by
            // header id), then open 'new' mode with that seed — so the
            // save POSTs a fresh header. Gated here inside
            // `!isTargetSplit`, so a target-split (incl. the split-
            // parent, ADR-0036) never offers it, exactly like Delete.
            // Only investment-shape rows (recognized action) can be
            // inverted; bank-shape cash rows lack posting roles, so the
            // item is omitted there rather than seeding a broken draft.
            if (isLedgerInvestmentAction(target.investmentAction)) {
                const action = target.investmentAction;
                // Duplicate and "Create reminder" seed from the same inverted
                // draft (the full leg set keyed by header id, the same inversion
                // the edit path uses).
                // Invert the FULL leg set (all accounts) into a draft — the
                // same /legs fetch the edit path uses. ADR-0080: the register
                // window no longer carries raw legs, so we read them from the
                // shared ['header-legs'] cache (instant when the editor has
                // warmed that header; a quick on-demand fetch otherwise). This
                // also fixes a latent bug — the old on-account legs map omitted
                // the off-account category / transfer / fee legs.
                const draftFromRow = async () =>
                    legsToDraft(
                        action,
                        accountId,
                        {
                            postedAt: target.postedAt,
                            payee: target.payee,
                            memo: target.headerMemo ?? target.memo,
                            checkNumber: target.checkNumber,
                        },
                        await queryClient.fetchQuery({
                            queryKey: ['header-legs', ledgerId, target.headerId],
                            queryFn: () => fetchHeaderLegs(ledgerId, target.headerId),
                        }),
                    );
                items.push({
                    id: 'duplicate',
                    label: 'Duplicate',
                    shortcutHint: '⌘D',
                    onSelect: () => {
                        void (async () => {
                            setDuplicateDraft(await draftFromRow());
                            startCreate();
                        })();
                    },
                });
                items.push({
                    id: 'create-reminder',
                    label: 'Create reminder',
                    onSelect: () => {
                        void (async () => {
                            setReminderFromInvestment({
                                sourceAccountId: accountId,
                                draft: await draftFromRow(),
                            });
                        })();
                    },
                });
            }
        }
        // "Show other side" navigates to the counterparty's register
        // with ?focus=<headerId> so that page scrolls + focuses the
        // row. On target-split rows this is the ONLY way to reach
        // the editable canonical event. On non-target rows it's a
        // convenience navigation to the counterparty's view.
        items.push({
            id: 'show-other-side',
            label: 'Show other side',
            onSelect: () => {
                if (target.counterpartyAccountId === null) return;
                void navigate({
                    to: '/ledgers/$ledgerId/accounts/$accountId',
                    params: {
                        ledgerId,
                        accountId: target.counterpartyAccountId,
                    },
                    search: { focus: target.headerId },
                } as unknown as Parameters<typeof navigate>[0]);
            },
            disabled: target.counterpartyAccountId === null,
        });
        items.push({
            id: 'raw',
            label: 'Show raw data',
            onSelect: () => setRawDataTarget(target),
        });
        if (!isTargetSplit) {
            items.push({
                id: 'delete',
                label: 'Delete',
                onSelect: () => setPendingDelete({ kind: 'single', target }),
                danger: true,
            });
        }
        return items;
    }

    // When edit mode opens, seed the editor's draft. Two paths:
    //   * Already-investment-shape row (legs carry posting_role per
    //     ADR-0029): invert via legsToDraft.
    //   * Sync-imported bank-shape row with ADR-0031 Phase 3
    //     classifier hints (action null but ingestActionHint set):
    //     seed via hintToDraft + capture the providerSecurityHint so
    //     save records the ticker → security mapping for future syncs.
    // Memoized on the editing header so the draft hook doesn't reset
    // on unrelated re-renders.
    // Full leg set for the row currently being edited — across ALL
    // accounts, not just this brokerage. legsToDraft needs the
    // off-account income / transfer / fee legs to pre-fill the
    // editor's category / transfer / fee fields. Loaded lazily
    // when editingHeaderId is set; the editor opens with the
    // partial draft (from the per-account legs) and rebuilds once
    // the full legs land — gives an instant open while the full
    // data fetches.
    const headerLegsQuery = useQuery({
        queryKey: ['header-legs', ledgerId, editingHeaderId],
        queryFn: () => fetchHeaderLegs(ledgerId, editingHeaderId!),
        enabled: editingHeaderId !== null,
        staleTime: 0,
    });

    const editingContext = useMemo(() => {
        if (editingHeaderId === null) return null;
        // Wait for the full leg set before opening the editor. The
        // editor's draft hook seeds via `useState(initial)`, which
        // only reads `initial` on its first render — re-rendering
        // with a fuller draft after the legs land has no effect.
        // The trade is a brief delay (~50ms typical) before the
        // editor appears; the row stays visible in the meantime.
        const legs = headerLegsQuery.data;
        if (!legs || legs.length === 0) return null;
        // Source `canonical` from the fresh per-header legs response
        // (staleTime: 0) rather than the windowed register cache.
        // The register window cache is populated at window-load and
        // stays put across saves; backfill side-effects on already-
        // imported rows (provider_security_mappings.UpsertAsync
        // setting ingest_security_id on every header with a matching
        // ticker hint) land in the DB but never propagate to the
        // cache. Reading canonical from `legs[0]` guarantees the
        // editor opens with the freshest ingest_* state — same
        // header-level data, projected per-leg by the view, so any
        // leg of this header carries it.
        //
        // Prefer the leg on the brokerage account being viewed (for
        // the rare case the off-account leg projection diverges);
        // fall back to legs[0] which the server orders by leg_index
        // ASC.
        const canonical = legs.find((l) => l.accountId === accountId) ?? legs[0]!;

        const headerFields = {
            postedAt: canonical.postedAt,
            payee: canonical.payee,
            memo: canonical.headerMemo ?? canonical.memo,
            checkNumber: canonical.checkNumber,
        };

        // Upgrade path: bank-shape sync row. Two sub-cases:
        //   * Classifier hint present → seed action + security
        //     pre-fills from the orchestrator's ingest_action_hint /
        //     ingest_security_id outputs.
        //   * Feed-imported but no hint (gap-fix per real-world data
        //     showing the classifier misses other formats) →
        //     open with a blank-action draft; the user picks
        //     everything manually. Manual bank-shape rows (origin =
        //     'manual') stay closed — they shouldn't appear in the
        //     investment register, and the matching server gate
        //     refuses the PATCH anyway.
        if (canonical.investmentAction === null) {
            const isFeedImported = canonical.origin !== 'manual';
            const hasHint = canonical.ingestActionHint !== null;
            if (!hasHint && !isFeedImported) return null;

            const seedAction = canonical.ingestActionHint as LedgerInvestmentAction | null;
            const draft = hintToDraft(
                seedAction,
                accountId,
                headerFields,
                legs,
                canonical.ingestSecurityId,
                // Mig 113: per-row OFX investment prefill carriers.
                // SimpleFIN brokerage rows leave these null; OFX
                // investment rows carry shares / unit price / fee
                // from the wire so the editor opens fully populated.
                canonical.ingestShares,
                canonical.ingestUnitPrice,
                canonical.ingestFee,
            );
            // Common rail (mig 114): every provider persists its
            // ticker hint at ingest time on
            // canonical.ingestSecurityTickerHint, so the Accept
            // flow records a provider_security_mapping with the
            // SAME identifier the next ingest will look up.
            //
            // Two fallback layers behind it:
            //   1. SimpleFIN rows synced BEFORE mig 114 have no
            //      persisted hint; re-run the description classifier
            //      on the payee to recover one. Goes away once those
            //      rows age out (or a backfill ships).
            //   2. Everything else: no hint → no mapping recorded,
            //      user picks the security, future Accepts of the
            //      same row chain still won't auto-resolve. The
            //      ProviderSecurityHint is server-side optional, so
            //      missing it never blocks the save.
            const persistedHint = canonical.ingestSecurityTickerHint;
            const fallbackHint = persistedHint === null
                && canonical.providerKey === 'simplefin'
                    ? classifySimpleFinDescription(canonical.payee).tickerHint
                    : null;
            const resolvedHint = persistedHint ?? fallbackHint;
            const providerSecurityHint = resolvedHint && canonical.providerKey
                ? {
                    providerKey: canonical.providerKey,
                    providerSecurityId: resolvedHint,
                }
                : undefined;
            return {
                headerId: editingHeaderId,
                initialDraft: draft,
                providerSecurityHint,
                needsReview: canonical.needsReview,
            };
        }

        // Already-investment-shape: legs carry posting_role; invert.
        const draft = legsToDraft(
            canonical.investmentAction as LedgerInvestmentAction,
            accountId,
            headerFields,
            legs,
        );
        return {
            headerId: editingHeaderId,
            initialDraft: draft,
            needsReview: canonical.needsReview,
        };
    }, [editingHeaderId, accountId, headerLegsQuery.data]);

    // Edge sentinels — shared with the bank register (ADR-0030 reuse).
    // Driven by the same `atTimelineHead` / `atTimelineTail` flags from
    // the windowed register; the oldest sentinel is labelled with the
    // tail entry's posted date. Memoized so virtuoso doesn't re-mount
    // the Header / Footer on every parent render.
    const oldestEntry = allEntries.at(-1) ?? null;
    const oldestPostedAtLabel = oldestEntry
        ? formatLedgerDate(entryStatusRow(oldestEntry).postedAt)
        : null;

    return (
        <MainArea>
            <RegisterTopBar
                ledgerId={ledgerId}
                ledger={ledger}
                accountName={account?.name ?? null}
                actions={
                    <>
                        {/* Single file-import affordance — OFX/QFX
                            (ADR-0031 Phase 4) and QIF (ADR-0042) are
                            distinguished by the picked file's extension. */}
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            title="Import statement file (OFX / QFX / QIF)"
                            onClick={() => setImportDialogOpen(true)}
                            className="gap-1.5"
                        >
                            <Upload className="h-3.5 w-3.5" aria-hidden />
                            Import
                        </Button>
                        {account?.feedConnectionId !== null
                        && account?.feedConnectionId !== undefined ? (
                            // Per-account SimpleFIN sync — parity with the bank
                            // register; only when this account is bound to a feed
                            // connection. Syncs JUST this account.
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                title="Sync this account from SimpleFIN (also refreshes prices)"
                                onClick={() => syncAccountMutation.mutate()}
                                disabled={syncAccountMutation.isPending}
                                className="ml-1 gap-1.5"
                            >
                                <RefreshCw
                                    className={
                                        'h-3.5 w-3.5 ' +
                                        (syncAccountMutation.isPending ? 'animate-spin' : '')
                                    }
                                    aria-hidden
                                />
                                {syncAccountMutation.isPending ? 'Syncing…' : 'Sync account'}
                            </Button>
                        ) : null}
                    </>
                }
            />

            {importDialogOpen && account ? (
                <ImportFileDialog
                    ledgerId={ledgerId}
                    accountId={accountId}
                    accountName={account.name}
                    onClose={() => setImportDialogOpen(false)}
                    onImported={() => {
                        register.refresh();
                        queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
                        queryClient.invalidateQueries({
                            queryKey: ['register-index-buckets', ledgerId, accountId],
                        });
                        queryClient.invalidateQueries({
                            queryKey: ['holdings', ledgerId, accountId],
                        });
                    }}
                />
            ) : null}
            {/* Portfolio summary + Activity / Holdings view switch. Always
                on top for investment accounts; the per-security table now
                lives in the Holdings view rather than stacked above the
                register, so a long holdings list no longer eats the
                register's vertical space. */}
            {isInvestment ? (
                <PortfolioBar
                    ledgerId={ledgerId}
                    accountId={accountId}
                    view={view}
                    onViewChange={setView}
                />
            ) : null}

            {isInvestment && view === 'holdings' ? (
                <HoldingsTable ledgerId={ledgerId} accountId={accountId} />
            ) : (
            <RegisterShell
                columns={INVESTMENT_REGISTER_COLS}
                initialLoaded={register.initialLoaded}
                initialError={register.initialError}
                isEmpty={allEntries.length === 0}
                filterActive={filterActive}
                toolbar={(
                    // Combined controls bar (fold #4): status-filter tabs
                    // (left) + New-transaction button & keyboard hint
                    // (right), one row. Identical to bank.
                    <RegisterControlsBar
                        statusFilter={statusFilter}
                        onStatusFilterChange={setStatusFilter}
                        sort={sort}
                        onSortChange={setSort}
                        isInvestment={isInvestment}
                        filter={filter}
                        onFilterChange={setFilter}
                        categories={(accountsQuery.data ?? []).filter(
                            (a) => a.accountType === 'category',
                        )}
                        tags={tagsQuery.data ?? []}
                        securities={securitiesQuery.data ?? []}
                        resultCount={filterResultCount}
                        statusCounts={statusCounts}
                        onNew={startCreate}
                        newDisabled={isCreatingNew || editingHeaderId !== null}
                        newButtonTitle="New investment transaction (N)"
                    />
                )}
                newTxnEditor={isCreatingNew && account !== null ? (
                    <InvestmentTxnRowEdit
                        ledgerId={ledgerId}
                        brokerageAccountId={accountId}
                        accounts={accountsQuery.data ?? []}
                        isTradeCommission={account.isTradeCommission}
                        cols={INVESTMENT_REGISTER_COLS}
                        onCancel={cancelEdit}
                        mode={{
                            kind: 'new',
                            // Duplicate flow: pre-seed the form with the
                            // source row's draft. undefined for a plain
                            // "+ New" (blank). 'new' mode always POSTs a
                            // fresh header regardless.
                            initialDraft: duplicateDraft ?? undefined,
                            onCreated: (headerId) => {
                                cancelEdit();
                                // Re-seed the register anchored at
                                // the new header so it appears at
                                // the top of the visible window with
                                // its surrounding context below.
                                register.refresh(headerId);
                                queryClient.invalidateQueries({
                                    queryKey: [
                                        'register-index-buckets',
                                        ledgerId,
                                        accountId,
                                    ],
                                });
                            },
                        }}
                    />
                ) : null}
                headerCells={(
                    <>
                    <RegisterLeadHeaderCells
                        selectAllRef={selectAllRef}
                        allVisibleSelected={allVisibleSelected}
                        onToggleAll={() => selection.toggleAll()}
                        disabled={visibleRowsForSelection.length === 0}
                        selectAllLabel="Select all transactions in this account"
                    />
                    <span role="columnheader">Date · Check #</span>
                    <span role="columnheader">Action</span>
                    <span role="columnheader">Payee · Memo</span>
                    <span role="columnheader">Category | Transfer · Fee</span>
                    <span role="columnheader">Security · Shares @ Price</span>
                    <span role="columnheader" className="text-right">Amount</span>
                    <span role="columnheader" className="text-right">Balance</span>
                    </>
                )}
            >
                <RegisterScrollSurface
                    scrollRef={setScrollParent}
                    scrollRegionId="register-scroll-region"
                    scrollTrack={sort.column === 'date' ? (
                        <RegisterScrollTrack
                            // Date-asc → oldest-first buckets so the rail's top
                            // matches the list's top (newest-first is the default).
                            buckets={sort.dir === 'asc' ? [...indexBuckets].reverse() : [...indexBuckets]}
                            currentYearMonth={viewportYearMonth ?? currentYearMonth}
                            onSeek={seekToBucket}
                        />
                    ) : null}
                >
                        <RegisterVirtualList
                            virtuosoRef={virtuosoRef}
                            scrollParent={scrollParent}
                            rows={displayRows}
                            getRowId={displayRowId}
                            getRowPostedAt={(row) =>
                                row.kind === 'txn'
                                    ? row.txn.postedAt
                                    : row.kind === 'split-parent'
                                        ? row.legs[0]?.postedAt
                                        : row.kind === 'split-leg'
                                            ? row.leg.postedAt
                                            : undefined
                            }
                            onViewportMonthChange={setViewportYearMonth}
                            onLoadNewer={register.loadNewer}
                            onLoadOlder={register.loadOlder}
                            atTimelineHead={register.atTimelineHead}
                            atTimelineTail={register.atTimelineTail}
                            oldestLabel={oldestPostedAtLabel}
                            renderRow={(_, row) => {
                                // ── Collapsed split-parent (ADR-0028
                                // refinement 2026-06): a bank-shape
                                // target-split cluster. Read-only here;
                                // edits happen on the originating
                                // register (Show other side). Reuses the
                                // synthesized aggregate computed once per
                                // group in `splitParentAggregates`. ──
                                if (row.kind === 'split-parent') {
                                    const focusId = `g:${row.groupId}`;
                                    const aggregate =
                                        splitParentAggregates.get(row.groupId);
                                    if (aggregate === undefined) return null;
                                    const canonical = row.legs[0]!;
                                    return (
                                        <RegisterRow
                                            strategy={investmentRowStrategy}
                                            variant="split-parent"
                                            row={aggregate}
                                            accountPaths={accountPaths}
                                            currency={currency}
                                            today={today}
                                            // Read-only target cluster — the
                                            // canonical event lives in the
                                            // originating register, so no
                                            // edit / double-click here.
                                            readOnly
                                            cmdClickToggles
                                            expand={{
                                                expanded: row.expanded,
                                                onToggle: () =>
                                                    toggleGroupExpanded(row.groupId),
                                                count: row.legs.length,
                                                groupId: row.groupId,
                                            }}
                                            focused={focusedRowId === focusId}
                                            onFocus={() => setFocusedRowId(focusId)}
                                            selected={selection.isSelected(
                                                canonical.headerId,
                                                canonical.createdAt,
                                            )}
                                            onToggleSelected={(shiftKey) =>
                                                shiftKey
                                                    ? selection.extendSelectionTo(canonical.headerId, orderedHeaderIds)
                                                    : selection.toggleId(canonical.headerId)
                                            }
                                            selectLabel={`Select split transaction with ${row.legs.length} legs`}
                                            status={resolveRowStatus(canonical, today)}
                                            cycleStatus={canonical.status}
                                            statusHeaderId={canonical.headerId}
                                            statusStatic={
                                                resolveRowStatus(canonical, today) === 'scheduled'
                                                || canonical.isPending
                                            }
                                            onCycleReconStatus={cycleReconStatus}
                                            onContextMenu={(anchor) =>
                                                setContextMenu({ anchor, target: aggregate })
                                            }
                                            title="Counter-side of a split transaction · click to focus · right-click for actions"
                                        />
                                    );
                                }
                                // ── Expanded split-leg: read-only,
                                // indented, blank balance. ──
                                if (row.kind === 'split-leg') {
                                    const leg = row.leg;
                                    return (
                                        <RegisterRow
                                            strategy={investmentRowStrategy}
                                            variant="split-leg"
                                            row={leg}
                                            accountPaths={accountPaths}
                                            currency={currency}
                                            today={today}
                                            focused={focusedRowId === leg.id}
                                            onFocus={() => setFocusedRowId(leg.id)}
                                            // Leg rows are read-only — selection /
                                            // status / edit / context menu belong
                                            // to the group, not the leg.
                                            selected={false}
                                            onToggleSelected={() => {}}
                                            status={leg.status}
                                            cycleStatus={leg.status}
                                            onCycleReconStatus={() => {}}
                                        />
                                    );
                                }
                                // ── Flat txn row (single event or single
                                // target split). ──
                                const txn = row.txn;
                                // ADR-0036: target-side rows share a headerId
                                // with the originating-side row but each has
                                // a distinct leg id. Identity for focus +
                                // edit-substitution must come off `txn.id`,
                                // not `txn.headerId`, or a click on one
                                // target row would select / open all the
                                // siblings.
                                const isTargetSplit =
                                    txn.accountPostingsOnHeader < txn.headerTotalPostings;
                                if (
                                    !isTargetSplit
                                    && editingHeaderId === txn.headerId
                                    && editingContext !== null
                                    && account !== null
                                ) {
                                    return (
                                        <InvestmentTxnRowEdit
                                            ledgerId={ledgerId}
                                            brokerageAccountId={accountId}
                                            accounts={accountsQuery.data ?? []}
                                            isTradeCommission={account.isTradeCommission}
                                            cols={INVESTMENT_REGISTER_COLS}
                                            onCancel={() => setEditingHeaderId(null)}
                                            mode={{
                                                kind: 'edit',
                                                headerId: editingContext.headerId,
                                                initialDraft: editingContext.initialDraft,
                                                providerSecurityHint: editingContext.providerSecurityHint,
                                                needsReview: editingContext.needsReview,
                                                onSaved: (entry) => {
                                                    setEditingHeaderId(null);
                                                    // Investment PATCH is a full reshape
                                                    // per ADR-0025 — leg amounts can shift,
                                                    // every downstream balance updates.
                                                    // Patch the saved row in place
                                                    // (mutateEntries with the server's
                                                    // returned entry) and refresh
                                                    // downstream balances via the bulk
                                                    // balances endpoint — no full window
                                                    // re-fetch, no virtuoso data-swap,
                                                    // no scroll jerk.
                                                    const savedHeaderId =
                                                        editingContext.headerId;
                                                    if (entry === null) {
                                                        register.removeEntries((e) =>
                                                            (e.kind === 'txn'
                                                                ? e.txn.headerId
                                                                : e.legs[0]!.headerId)
                                                                === savedHeaderId);
                                                    } else {
                                                        // A posted-date change moves the
                                                        // row's sort position; re-seed the
                                                        // window anchored at it so it
                                                        // relocates (the re-fetch brings
                                                        // fresh balances too, so skip the
                                                        // in-place patch + balance refresh).
                                                        const newPostedAt =
                                                            entry.kind === 'txn'
                                                                ? entry.txn.postedAt
                                                                : entry.legs[0]!.postedAt;
                                                        if (repositionIfDateChanged(
                                                            savedHeaderId, newPostedAt,
                                                        )) {
                                                            queryClient.invalidateQueries({
                                                                queryKey: [
                                                                    'register-index-buckets',
                                                                    ledgerId,
                                                                    accountId,
                                                                ],
                                                            });
                                                            return;
                                                        }
                                                        register.mutateEntries((e) => {
                                                            const id = e.kind === 'txn'
                                                                ? e.txn.headerId
                                                                : e.legs[0]!.headerId;
                                                            return id === savedHeaderId ? entry : e;
                                                        });
                                                    }
                                                    void refreshLoadedBalances();
                                                    queryClient.invalidateQueries({
                                                        queryKey: [
                                                            'register-index-buckets',
                                                            ledgerId,
                                                            accountId,
                                                        ],
                                                    });
                                                },
                                            }}
                                        />
                                    );
                                }
                                // Focus identity: headerId on the
                                // originating side (survives PATCH
                                // reshape), leg id on a single target row.
                                const rowFocusId = isTargetSplit
                                    ? txn.id
                                    : txn.headerId;
                                return (
                                    <RegisterRow
                                        strategy={investmentRowStrategy}
                                        variant="txn"
                                        row={txn}
                                        accountPaths={accountPaths}
                                        currency={currency}
                                        today={today}
                                        cmdClickToggles
                                        readOnly={isTargetSplit}
                                        needsReview={txn.needsReview}
                                        focused={focusedRowId === rowFocusId}
                                        onFocus={() => setFocusedRowId(rowFocusId)}
                                        onDoubleClickEdit={() => startEdit(txn.headerId)}
                                        onContextMenu={(anchor) =>
                                            setContextMenu({ anchor, target: txn })
                                        }
                                        onCycleReconStatus={cycleReconStatus}
                                        status={resolveRowStatus(txn, today)}
                                        cycleStatus={txn.status}
                                        statusHeaderId={txn.headerId}
                                        statusStatic={
                                            resolveRowStatus(txn, today) === 'scheduled'
                                            || txn.isPending
                                        }
                                        selected={selection.isSelected(
                                            txn.headerId,
                                            txn.createdAt,
                                        )}
                                        onToggleSelected={(shiftKey) =>
                                            shiftKey
                                                ? selection.extendSelectionTo(txn.headerId, orderedHeaderIds)
                                                : selection.toggleId(txn.headerId)
                                        }
                                        selectLabel={`Select transaction ${txn.id}`}
                                        title="Click to focus · double-click to edit · right-click for actions"
                                    />
                                );
                            }}
                        />
                </RegisterScrollSurface>
                {/* Multi-select action bar (ADR-0024) — shared with the
                    bank register. Rendered UNCONDITIONALLY for parity
                    with bank: the footer's `alwaysVisible` default makes
                    it a persistent "N rows loaded" status strip, with the
                    selection Σ + action buttons appearing only once a row
                    is checked. No `extraActions` (the bank-only
                    Categorize / Tag placeholders don't apply here). */}
                <RegisterBulkActionBar
                    selectedCount={selectedCount}
                    selectedSum={selectedSum}
                    currency={currency}
                    loading={register.loadingOlder || register.loadingNewer}
                    bulkDeleteDisabled={bulkDeleteDisabled}
                    bulkDeleteDisabledTitle={bulkDeleteDisabledTitle}
                    onBulkSetReconStatus={(status) =>
                        bulkReconStatusMutation.mutate(status)
                    }
                    onRequestBulkDelete={() => setPendingDelete({ kind: 'bulk' })}
                    onClearSelection={() => selection.clear()}
                    statusFilter={statusFilter}
                    onBulkUnhide={recovery.onBulkUnhide}
                    bulkUnhidePending={recovery.bulkUnhidePending}
                    onOpenMoveDialog={recovery.openMoveDialog}
                    moveDisabled={selectionHasReadOnly}
                    moveDisabledTitle="Selection includes a split counter-side whose canonical owner is elsewhere. Move it from the source register."
                />
            </RegisterShell>
            )}
            <RegisterDateJumpPopover
                buckets={[...indexBuckets]}
                onSeek={seekToBucket}
            />

            {/* Right-click context menu — opens at the clicked row's
                anchor coords; items dispatched by row callbacks. */}
            {contextMenu !== null ? (
                <ContextMenu
                    anchor={contextMenu.anchor}
                    items={buildRowMenuItems(contextMenu.target)}
                    onClose={closeContextMenu}
                />
            ) : null}

            <MoveToAccountDialog
                open={recovery.moveDialogOpen}
                accounts={accountsQuery.data ?? []}
                sourceAccountId={accountId}
                count={selection.summary?.count ?? 0}
                pending={recovery.bulkMovePending}
                error={recovery.moveError}
                onConfirm={recovery.onMoveConfirm}
                onCancel={recovery.closeMoveDialog}
            />

            {reminderFromInvestment !== null ? (
                <ReminderEditorDialog
                    ledgerId={ledgerId}
                    reminderId={null}
                    fromInvestmentTransaction={reminderFromInvestment}
                    onClose={() => setReminderFromInvestment(null)}
                    onSaved={() => setReminderFromInvestment(null)}
                />
            ) : null}

            {/* Delete confirmation (single + bulk) — shared with the bank
                register via RegisterDeleteConfirm. Single cites the row
                (hard-delete vs soft-hide by external_id); bulk counts headers
                and requires a typed confirm above the ADR-0024 threshold. */}
            <RegisterDeleteConfirm
                pending={pendingDelete}
                selectedCount={selectedCount}
                allMode={selection.selection.kind === 'all'}
                isConfirming={
                    pendingDelete?.kind === 'single'
                        ? deleteMutation.isPending
                        : bulkDeleteMutation.isPending
                }
                onConfirmSingle={(target) => {
                    setDeleteError(null);
                    deleteMutation.mutate(target);
                }}
                onConfirmBulk={() => bulkDeleteMutation.mutate()}
                onCancel={() => setPendingDelete(null)}
            />

            {deleteError !== null ? (
                <ConfirmDialog
                    open
                    title="Couldn't delete this row"
                    body={deleteError}
                    confirmLabel="OK"
                    variant="neutral"
                    onConfirm={() => setDeleteError(null)}
                    onCancel={() => setDeleteError(null)}
                />
            ) : null}

            {/* Diagnostic: raw-data dump for the right-clicked row.
                Useful when debugging classifier coverage — the user
                sees the exact payee text + the row's full SPA-side
                shape including the new ADR-0031 hint fields. The
                legs are fetched on demand from the shared /legs cache
                (ADR-0080), so multi-leg transactions show every leg's
                amount + account binding across all accounts. */}
            {rawDataTarget !== null ? (
                <RawDataModal
                    target={rawDataTarget}
                    legs={rawDataLegsQuery.data ?? []}
                    onClose={() => setRawDataTarget(null)}
                />
            ) : null}
        </MainArea>
    );
}

/**
 * Diagnostic modal that pretty-prints the right-clicked row + its
 * legs as JSON. No styling beyond the bare minimum — this surface
 * exists for debugging (especially classifier coverage on sync
 * rows). Esc + backdrop click + close button all dismiss.
 */
function RawDataModal({
    target,
    legs,
    onClose,
}: {
    target: InvestmentRowType;
    legs: readonly InvestmentRowType[];
    onClose: () => void;
}) {
    // The PROVIDER's verbatim JSON is the headline payload — the user
    // asked for "raw SimpleFIN data" and that's what they get. Pretty-
    // printed so the formatting is scannable. NULL when the row was
    // synced before storage existed OR is manual/MD-imported; in that
    // case we fall back to the SPA-side row view (still useful for
    // debugging the rest of the pipeline).
    const provider = target.providerRawPayload;
    const providerPretty = provider
        ? safePrettyJson(provider)
        : null;
    const fallback = JSON.stringify({ row: target, legs }, null, 2);
    const json = providerPretty ?? fallback;
    const hasProvider = providerPretty !== null;

    return (
        <Modal open onClose={onClose} titleId="raw-data-title" className="max-w-3xl">
            <div className="flex max-h-[80vh] flex-col gap-3 p-4">
                <div className="flex items-center justify-between gap-2">
                    <h2 id="raw-data-title" className="text-sm font-semibold text-text">
                        {hasProvider ? 'Raw provider data' : 'Raw row data'}
                        {!hasProvider ? (
                            <span className="ml-2 text-[0.6875rem] font-normal text-text-muted">
                                (provider payload not captured — synced before storage existed; re-sync to backfill)
                            </span>
                        ) : null}
                    </h2>
                    <button
                        type="button"
                        onClick={onClose}
                        className="rounded px-2 py-1 text-xs text-text-muted hover:bg-surface-hover hover:text-text"
                        aria-label="Close"
                    >
                        Close
                    </button>
                </div>
                <pre className="flex-1 overflow-auto rounded bg-surface-muted p-3 text-[0.6875rem] font-mono leading-tight text-text-default">
                    {json}
                </pre>
                <div className="flex justify-end">
                    <button
                        type="button"
                        onClick={() => {
                            void navigator.clipboard.writeText(json);
                        }}
                        className="rounded border border-border px-2 py-1 text-xs text-text-muted hover:bg-surface-hover hover:text-text"
                    >
                        Copy JSON
                    </button>
                </div>
            </div>
        </Modal>
    );
}

function safePrettyJson(raw: string): string {
    try {
        return JSON.stringify(JSON.parse(raw), null, 2);
    } catch {
        // Malformed JSON: show the original string so we can still
        // debug. Shouldn't happen in practice — the server stores
        // the value as JSONB which validates on write.
        return raw;
    }
}

/**
 * Narrow the account-scoped window to investment entries on the `kind`
 * discriminant (ADR-0030 §2), mirroring the bank register's
 * `narrowToBankEntries`. RegisterRouter only dispatches an investment account
 * here, so every row is `kind: 'investment'`; a non-investment row would be a
 * routing defect and is dropped rather than coerced. The server already
 * collapsed each header into one event (ADR-0080) — this only narrows types.
 */
function narrowToInvestmentEntries(
    entries: readonly RegisterEntry[],
): RegisterEntryOf<InvestmentRowType>[] {
    const out: RegisterEntryOf<InvestmentRowType>[] = [];
    for (const entry of entries) {
        if (entry.kind === 'txn') {
            if (entry.txn.kind !== 'investment') continue;
            out.push({ kind: 'txn', txn: entry.txn, groupId: null, legs: null });
            continue;
        }
        const legs = entry.legs.filter(
            (l): l is InvestmentRowType => l.kind === 'investment',
        );
        if (legs.length === 0) continue;
        out.push({ kind: 'group', txn: null, groupId: entry.groupId, legs });
    }
    return out;
}
