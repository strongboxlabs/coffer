import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { type VirtuosoHandle } from 'react-virtuoso';

import { Upload, RefreshCw } from 'lucide-react';

import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';
import { Button } from '@/components/ui/Button';
import { MainArea } from '@/components/ui/SidebarLayout';
import {
    ApiError,
    approveInvestmentTransaction,
    bulkDeleteTransactions,
    bulkSetReconStatus,
    deleteInvestmentTransaction,
    deleteTransaction,
    fetchAccounts,
    fetchHeaderLegs,
    fetchSecurities,
    fetchTags,
    syncAccount,
} from '@/lib/api';
import { buildAccountPathMap } from '@/lib/accountPath';
import {
    buildDisplayRows,
    rowHasHeader,
    type DisplayRow,
    type RegisterEntryOf,
} from '@/lib/splitCollapse';
import { useSelection } from '@/lib/useSelection';
import { entryPostedAt } from '@/lib/useWindowedRegister';
import { invalidateHomeReads } from '@/lib/registerInvalidation';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { systemCategoryIds } from '@/lib/categoryChip';
import { rowActionsFor } from '../shell/viewerRowActions';
import type {
    AccountSummary,
    BulkDeleteResponse,
    BulkReconStatusResponse,
    DeleteTransactionResponse,
    ReconStatus,
    RegisterEntry,
} from '@/lib/types';

import { formatLedgerDate } from '@/lib/dates';

import { PortfolioBar, HoldingsTable, type AccountView } from '../../HoldingsPanel';
import { ACTION_LAYOUTS } from '../../investment-edit/actionLayout';
import { investmentEditContext } from '../../investment-edit/editContext';
import { InvestmentTxnRowEdit } from '../../investment-edit/InvestmentTxnRowEdit';
import { legsToDraft } from '../../investment-edit/legsToDraft';
import type { InvestmentTxnDraft } from '../../investment-edit/validation';
import { ReminderEditorDialog } from '../../reminders/ReminderEditorDialog';
import { TagColorsProvider } from '@/components/tags/TagColorsContext';
import { RawDataModal } from '../shell/RawDataModal';
import { REGISTER_SELECT_ALL_LABEL } from '../shell/registerLabels';
import { RegisterTopBar } from '../shell/RegisterTopBar';
import { RegisterShell } from '../shell/RegisterShell';
import { accountReadError } from '../shell/accountReadError';
import { RegisterScrollSurface } from '../shell/RegisterScrollSurface';
import { ImportFileDialog } from '../shell/ImportFileDialog';
import {
    invalidateRegisterStatusCounts,
    useRegisterController,
} from '../shell/useRegisterController';
import { useRegisterFocusHome } from '../shell/useRegisterFocusHome';
import { useRegisterKeyboardNav } from '../shell/useRegisterKeyboardNav';
import { RegisterDeleteConfirm, type PendingDelete } from '../shell/RegisterDeleteConfirm';
import { RegisterBulkActionBar } from '../shell/RegisterBulkActionBar';
import { selectionFooterCounts } from '../shell/selectionFooterCounts';
import { useRegisterBulkRecovery } from '../shell/useRegisterBulkRecovery';
import { MoveToAccountDialog } from '../shell/MoveToAccountDialog';
import { RegisterScrollTrack } from '../RegisterScrollTrack';
import { RegisterDateJumpPopover } from '../RegisterDateJumpPopover';
import { RegisterControlsBar } from '../shell/RegisterControlsBar';
import { DEFAULT_SORT, type RegisterSortState } from '../shell/registerSort';
import { RegisterLeadHeaderCells } from '../shell/RegisterRowLead';
import { RegisterVirtualList } from '../shell/RegisterVirtualList';
import { ensureRowFullyVisible } from '../shell/ensureRowFullyVisible';
import {
    arrivalStatusOf,
    passesStatusFilter,
    resolveRowStatus,
    toSelectionStatusFilter,
    type RegisterArrivalStatus,
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
 * Per-display-row focus identity for the investment register. Mirrors bank's
 * `displayRowId`, with one nuance the investment register keeps: an editable
 * row uses `headerId`, so focus survives an investment PATCH (a reshape keeps
 * the headerId, but a leg the new shape has no room for is replaced); a
 * read-only row uses its leg
 * `id`, because the lines of one split seen from here share a headerId and
 * each is its own row.
 */
function displayRowId(row: DisplayRow<InvestmentRowType>): string {
    if (row.kind === 'txn') {
        return row.txn.isReadOnly ? row.txn.id : row.txn.headerId;
    }
    if (row.kind === 'split-parent') return `g:${row.groupId}`;
    return row.leg.id;
}

/** This register never expands anything: it has no group rows. */
const NO_EXPANDED_GROUPS: ReadonlySet<string> = new Set();

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
    // `?focus=<headerId>` — an arrival anchor. Set by "Show other side" from a
    // bank row, by this register's own "Show other side" (a transfer_shares
    // counterparty is always another brokerage), and by every row of the
    // Security Detail transactions table, whose rows are security-bearing legs
    // and so always land on a brokerage account. The register seeds the window
    // on that row and focuses it; NULL when navigating in normally.
    const search = useSearch({ strict: false }) as { focus?: string; status?: RegisterArrivalStatus };
    const focusFromUrl = search.focus;

    // A viewer reads the register and writes nothing: no new transaction, no
    // editor, no write item in the row menu, no bulk action, import or sync.
    const { ledger: visibleLedger, canEdit } = useLedgerRole(ledgerId);
    const ledger = visibleLedger ?? null;
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

    const account = accountsQuery.data?.find((a: AccountSummary) => a.id === accountId) ?? null;
    // The account's currency, which every figure here is in. The register's page
    // carries none, so until the account list has the account the list keeps its
    // loading state rather than print figures in a currency they are not in.
    const currency = account?.currencyCode ?? null;
    // Account-id → full slash path, so the register's category / transfer
    // chips render their parent→child chain (ADR-0068 Slice A). Covers
    // every account incl. categories (categories ARE accounts).
    const accountPaths = useMemo(
        () => buildAccountPathMap(accountsQuery.data ?? []),
        [accountsQuery.data],
    );
    // The ledger's Uncategorized, whose chip is always neutral.
    const systemIds = useMemo(() => systemCategoryIds(accountsQuery.data), [accountsQuery.data]);
    const isInvestment = account?.accountType === 'investment';

    // Two views share this page: the activity register (default) and the
    // Holdings positions table. Local state — Activity is the primary view,
    // Holdings is a quick peek; both keep the same PortfolioBar on top.
    const [view, setView] = useState<AccountView>('activity');

    const queryClient = useQueryClient();

    // Shared by import and undo. The windowed register re-reads on register.refresh(),
    // not on cache invalidation (ADR-0079); holdings and lots derive from the same
    // legs, so they move whichever direction the rows went — an undone buy changes
    // them exactly as much as the buy did.
    function refreshAfterImportChange() {
        register.refresh();
        queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
        invalidateHomeReads(queryClient, ledgerId);
        queryClient.invalidateQueries({
            queryKey: ['register-index-buckets', ledgerId, accountId],
        });
        queryClient.invalidateQueries({ queryKey: ['holdings', ledgerId, accountId] });
    }


    // Status filter (All / Cleared / Uncleared N / Scheduled N) — the
    // same shared tabs the bank register uses (ADR-0030 reuse). Applied
    // client-side over the aggregated (displayed) rows, and fed into
    // useSelection so a 'select-all' click selects only rows matching
    // the active filter — identical semantics to bank. Seeded from
    // `?status=` on arrival (Needs review or Hidden), as the bank register is.
    const [statusFilter, setStatusFilter] = useState<StatusFilter>(
        arrivalStatusOf(search.status) ?? 'all');
    // Column sort (mig 166). Threaded to the controller (windowed read only)
    // and the controls bar's Sort ▾ menu; an investment register also offers
    // the Security / Shares / Price / Action columns.
    const [sort, setSort] = useState<RegisterSortState>(DEFAULT_SORT);

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

    // Shared register-container controller (the same hook the bank
    // register uses; per ADR-0028 the windowing behavior is
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
        reseatIfMoved,
        patchHeadersStatus,
        cycleReconStatus,
    } = useRegisterController({
        ledgerId,
        accountId,
        pageSize: PAGE_SIZE,
        statusFilter,
        filter,
        sort,
        focusHeaderId: focusFromUrl,
    });

    // When any user filter is active the index buckets already reflect the
    // same filtered set, so their total IS the match count (null hides the
    // count chip). Mirrors bank.
    const filterActive = isRegisterFilterActive(filter);
    const filterResultCount = filterActive
        ? indexBuckets.reduce((sum, b) => sum + b.count, 0)
        : null;

    // Investment EVENTS are already collapsed server-side (ADR-0080), and a
    // split's lines seen from here are each their own row, so there is no
    // client-side entry transform beyond narrowing the row type.
    const allEntries = useMemo(
        () => narrowToInvestmentEntries(register.entries),
        [register.entries],
    );

    // Status is derived from an entry's canonical row (mirrors bank's
    // `entryStatusRow`). Every entry here is a flat row
    // (`narrowToInvestmentEntries`), so that is `entry.txn`; the `legs[0]`
    // branch only satisfies the shared entry type.
    const entryStatusRow = (
        entry: RegisterEntryOf<InvestmentRowType>,
    ): InvestmentRowType =>
        entry.kind === 'txn' ? entry.txn : entry.legs[0]!;

    // Status counts over the FULL aggregated list (pre-filter) — drives
    // the Uncleared / Scheduled count badges on the tabs, same as bank
    // computes its counts over `allEntries`. Reads the canonical row of
    // each entry, one per entry.
    // The displayed list — the aggregated entries that pass the active
    // status filter. This is what the user sees + navigates + selects,
    // mirroring bank's `visibleEntries`. Every entry is a flat row,
    // filtered by its own status. Everything downstream (displayRows, keyboard
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
    // every entry is a flat row, so each passes through as one `txn` row.
    // Generic over `InvestmentRowType` so the row components
    // receive the narrowed type — same pipeline the bank register uses.
    const displayRows = useMemo(
        () => buildDisplayRows<InvestmentRowType>(visibleEntries, NO_EXPANDED_GROUPS),
        [visibleEntries],
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

    // Keyboard navigation + focus cursor (shared with the bank
    // register). Iterates `displayRows` (post-aggregation + status
    // filter) so ArrowDown steps through the exact row list the user
    // sees. Focus id comes from `displayRowId`: headerId on editable
    // rows (survives an investment PATCH reshape), leg id on read-only
    // rows. `N` opens the new-transaction editor; `Enter` edits the
    // focused row only when it is editable (opposite-side lines and
    // event counterparts are read-only).
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
        // A viewer creates and edits nothing, so neither key is bound. Nor is
        // `n` before the account's currency is known, as New transaction is
        // disabled until then: a press would open the editor by itself later.
        onCreate: canEdit && currency !== null ? startCreate : undefined,
        onEnterRow: (currentId, e) => {
            if (!canEdit) return;
            const row = displayRows.find((r) => displayRowId(r) === currentId);
            // A read-only row opens only in the register that owns it; leave
            // the default intact (no preventDefault) on one.
            if (row?.kind !== 'txn') return;
            if (row.txn.isReadOnly) return;
            e.preventDefault();
            startEdit(row.txn.headerId);
        },
    });
    // Focus taken away with the bulk bar's buttons or a closing editor goes
    // to the row list, never the page (ADR-0023 §L).
    useRegisterFocusHome(scrollParent, {
        editing: editingHeaderId !== null || isCreatingNew,
        hasSelection: selection.hasSelection,
    });

    // Seed focus from the resolved arrival anchor, once per arrival, and scroll
    // it into view.
    //
    // Resolved by HEADER ID against displayRows — never by index. The hook
    // counts ENTRIES while this list renders ROWS (after filtering by status),
    // and the two are not equal. The hook reports an anchor only when the server actually placed it
    // in the window, so there is no case where this focuses a row nobody asked
    // for: an anchor the server declined is simply never reported, and one it
    // honoured but the status filter then hid is not found here.
    //
    // The gate holds the anchor id, so an anchored refresh onto the SAME row
    // (post-create, the other caller) still re-seeds. It resets on a view
    // change because the Holdings toggle unmounts the list entirely — without
    // that, returning to Activity would find the gate already satisfied and
    // never restore focus.
    const seededFocusForRef = useRef<string | null>(null);
    useEffect(() => {
        if (view === 'holdings') {
            seededFocusForRef.current = null;
            return;
        }
        const anchorId = register.focusAnchorHeaderId;
        if (anchorId === null) {
            seededFocusForRef.current = null;
            return;
        }
        if (seededFocusForRef.current === anchorId) return;

        const localIndex = displayRows.findIndex((r) => rowHasHeader(r, anchorId));
        if (localIndex < 0) return;

        setFocusedRowId(displayRowId(displayRows[localIndex]!));
        seededFocusForRef.current = anchorId;
        const handle = window.setTimeout(() => {
            // Local index: this register feeds virtuoso plain local indices, and
            // scrollIntoView takes one. No `align` → virtuoso's "smart" mode,
            // which is a no-op when the row is already in the viewport —
            // including when it is clipped at the top edge, which is why the
            // correction below exists.
            virtuosoRef.current?.scrollIntoView({
                index: localIndex,
                done: () => ensureRowFullyVisible(scrollParent, anchorId),
            });
        }, 100);
        return () => window.clearTimeout(handle);
    }, [register.focusAnchorHeaderId, displayRows, setFocusedRowId, view, scrollParent]);
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
            invalidateHomeReads(queryClient, ledgerId);
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
            invalidateHomeReads(queryClient, ledgerId);
            queryClient.invalidateQueries({ queryKey: ['holdings', ledgerId, accountId] });
            queryClient.invalidateQueries({
                queryKey: ['register-index-buckets', ledgerId, accountId],
            });
        },
        onError: (err) => {
            setDeleteError(err.detail || err.message || 'Delete failed.');
        },
    });

    // Accept an imported row as-is, from the row menu — the bank register has
    // offered this since slice 2c and this one never did, so the only way to
    // accept an investment row was to open the editor and re-save every field
    // through a wholesale PATCH. Going through the dedicated route matters for
    // the same reason: a PATCH IS the new state of the row, so accepting via
    // one writes back whatever the client reconstructed, and accepting should
    // change nothing.
    const approveMutation = useMutation<RegisterEntry[], ApiError, string>({
        mutationFn: (headerId) =>
            approveInvestmentTransaction(ledgerId, headerId, accountId),
        onSuccess: (_entry, headerId) => {
            // Flip the flag on the loaded row rather than refetching the window:
            // the row does not move and a refresh would cost the scroll position.
            register.mutateEntries((entry) => {
                const id = entry.kind === 'txn'
                    ? entry.txn.headerId
                    : entry.legs[0]!.headerId;
                if (id !== headerId) return entry;
                return entry.kind === 'txn'
                    ? { ...entry, txn: { ...entry.txn, needsReview: false } }
                    : { ...entry, legs: entry.legs.map((l) => ({ ...l, needsReview: false })) };
            });
            // Accepting clears the row's review flag, which is what lights the
            // sidebar dot — invalidate so it resets live when this was the
            // account's last pending item.
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            invalidateHomeReads(queryClient, ledgerId);
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
            invalidateHomeReads(queryClient, ledgerId);
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

    // The rows a bulk change acts on, for the delete and move confirmations
    // — defer to the server summary when present; fall back to the explicit
    // ids count while the debounced summary query is still in flight. Same
    // fallback as bank.
    const selectedCount =
        selection.summary?.count ??
        (selection.selection.kind === 'explicit'
            ? selection.selection.headerIds.length
            : 0);
    // A real account's Σ is native, in the currency the summary names (ADR-0114 D7).
    const selectedSum = selection.summary === null ? 0 : selection.summary.sumOnAccount;
    const selectedSumCurrency = currency === null ? null : selection.summary?.currencyCode ?? currency;
    // Footer count: every selected row, read-only rows included, with the
    // read-only part named beside it ("N selected · M read-only").
    const footerCounts = useMemo(
        () => selectionFooterCounts(
            selection.selection,
            selection.summary,
            selection.isSummaryCurrent,
            displayRows,
        ),
        [selection.selection, selection.summary, selection.isSummaryCurrent, displayRows],
    );

    // Read-only rows (the row's `isReadOnly`): a split line seen from its
    // other side, or an investment event's counterpart; its owner is
    // another register. Bulk Delete sends the selection to
    // `/transactions/bulk-delete`, so including a read-only row
    // would either mutate a cross-domain header or 422 mid-batch.
    // Only meaningful for 'explicit' selections (returns false in
    // 'all'-mode): in 'all'-mode the server restricts bulk ops to
    // headers with a row this register can edit, by the same stored
    // read-only flag the row carries, so unloaded read-only rows are
    // excluded server-side — nothing for the client to audit. Gate
    // exactly as bank does.
    const selectionHasReadOnly = useMemo(() => {
        if (selection.selection.kind !== 'explicit') return false;
        const selectedIds = new Set(selection.selection.headerIds);
        for (const row of displayRows) {
            // A flat `txn` row is read-only per the stored kind
            // (`probe.isReadOnly`). The `split-parent` branch only
            // satisfies the shared row type; split-leg rows share their
            // parent's header (covered by the parent).
            const probe =
                row.kind === 'txn'
                    ? row.txn
                    : row.kind === 'split-parent'
                        ? row.legs[0]!
                        : null;
            if (probe === null) continue;
            if (!selectedIds.has(probe.headerId)) continue;
            if (probe.isReadOnly) return true;
        }
        return false;
    }, [selection.selection, displayRows]);

    // Disabled only when an explicit selection includes a read-only
    // row. 'all'-mode is now allowed: the server restricts all-mode
    // bulk ops to headers with a row this register can edit, so it can
    // never touch a row shown read-only here. The typed-confirm
    // dialog still guards large all-mode deletes.
    const bulkDeleteDisabled = selectionHasReadOnly;
    const bulkDeleteDisabledTitle = selectionHasReadOnly
        ? 'Selection includes rows whose canonical owner is elsewhere (split counter-side). Delete those from the source register.'
        : 'Delete the active selection';

    function buildRowMenuItems(target: InvestmentRowType): ContextMenuItem[] {
        // Read-only rows (a split line seen from its other side, an
        // investment event's counterpart) are edited in the register that
        // owns them. Hide Edit, Duplicate + Delete; keep "Show raw data"
        // for inspection and "Show other side" so the user can jump to the
        // owner to edit (parity with bank register's same affordance).
        const isReadOnly = target.isReadOnly;
        const items: ContextMenuItem[] = [];
        // Accept sits at the top when the row still carries the review flag,
        // matching the bank register: it is the primary action on a freshly
        // imported row, and burying it behind Edit is what made the editor the
        // only route to it. A read-only row is excluded alongside Edit and
        // Delete.
        if (target.needsReview && !isReadOnly) {
            items.push({
                id: 'accept',
                label: 'Accept',
                onSelect: () => approveMutation.mutate(target.headerId),
            });
        }
        if (!isReadOnly) {
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
            // `!isReadOnly`, so a read-only row (incl. a read-only
            // split-parent) never offers it, exactly like Delete.
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
                            transactedAt: target.transactedAt,
                            payee: target.payee,
                            // A duplicate copies what this register shows as
                            // the copy's own, tags included.
                            memo: target.memo,
                            checkNumber: target.checkNumber,
                            tags: target.tags,
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
        // row. On a read-only row this is the ONLY way to reach the
        // register that can edit it. On an editable row it's a
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
        if (!isReadOnly) {
            items.push({
                id: 'delete',
                label: 'Delete',
                onSelect: () => setPendingDelete({ kind: 'single', target }),
                danger: true,
            });
        }
        return items;
    }

    // When edit mode opens, seed the editor's draft from the full leg set of
    // the row being edited — across ALL accounts, not just this brokerage:
    // the off-account income / transfer / fee legs pre-fill the editor's
    // category / transfer / fee fields. Loaded lazily when editingHeaderId is
    // set. Memoized on the editing header so the draft hook doesn't reset on
    // unrelated re-renders.
    const headerLegsQuery = useQuery({
        queryKey: ['header-legs', ledgerId, editingHeaderId],
        queryFn: () => fetchHeaderLegs(ledgerId, editingHeaderId!),
        enabled: editingHeaderId !== null,
        staleTime: 0,
    });

    // The edit context comes from investment-edit/editContext.ts. It waits for
    // the full leg set before opening the editor: the editor's draft hook
    // seeds via `useState(initial)`, which only reads `initial` on its first
    // render, so a fuller draft after the legs land would have no effect. The
    // trade is a brief delay (one request) before the editor appears; the row
    // stays visible in the meantime.
    //
    // The legs are the fresh per-header response (staleTime: 0), not the
    // windowed register cache: the window is populated at load and stays put
    // across saves, so backfill side-effects on already-imported rows
    // (provider_security_mappings.UpsertAsync setting ingest_security_id on
    // every header with a matching ticker hint) land in the DB but never reach
    // it. The fresh legs open the editor with the freshest ingest_* state.
    const editingContext = useMemo(
        () => (editingHeaderId === null
            ? null
            : investmentEditContext(editingHeaderId, accountId, headerLegsQuery.data)),
        [editingHeaderId, accountId, headerLegsQuery.data],
    );

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
        // Tag chips take their colour from here (ADR-0077 D4): the rows carry
        // NAMES only, and the provider joins name -> colour from the same
        // ['tags', ledgerId] query the filter and the Tags panel use, so a
        // recolor there repaints this register too, as it does the bank
        // register.
        <TagColorsProvider ledgerId={ledgerId}>
        <MainArea>
            <RegisterTopBar
                ledgerId={ledgerId}
                ledger={ledger}
                accountName={account?.name ?? null}
                actions={
                    <>
                        {/* Single file-import affordance — OFX/QFX
                            (ADR-0031 Phase 4) and QIF (ADR-0042) are
                            distinguished by the picked file's extension.

                            Gated on isActive, as the bank register gates it: a
                            retired brokerage was still inviting new rows into an
                            account the user had deliberately closed, and the
                            import would succeed. Deactivating an account is a
                            statement that nothing more should land in it. */}
                        {canEdit && account?.isActive ? (
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                title="Import statement file (OFX / QFX / QIF)"
                                onClick={() => setImportDialogOpen(true)}
                                className="gap-1.5"
                            >
                                <Upload className="size-icon-sm" aria-hidden />
                                Import
                            </Button>
                        ) : null}
                        {canEdit
                        && account?.isActive
                        && account?.feedConnectionId !== null
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
                                pending={syncAccountMutation.isPending}
                                pendingLabel={<><RefreshCw className="size-icon-sm animate-spin" aria-hidden />Syncing…</>}
                                className="ml-1 gap-1.5"
                            >
                                <RefreshCw className="size-icon-sm" aria-hidden />
                                Sync account
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
                    accountKind="investment"
                    importProviderKey={account.importProviderKey}
                    onClose={() => setImportDialogOpen(false)}
                    onImported={refreshAfterImportChange}
                    // Undo moves the same rows, so it needs the same refresh —
                    // including holdings, which an undone buy changes just as much as
                    // the buy did.
                    onUndone={refreshAfterImportChange}
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
                initialLoaded={register.initialLoaded && currency !== null}
                // The list also waits for the account (its currency), so the
                // account read's failure is the list's too.
                initialError={register.initialError ?? accountReadError(accountsQuery, account)}
                isEmpty={allEntries.length === 0}
                filterActive={filterActive}
                toolbar={(
                    // Combined controls bar (fold #4): status-filter tabs
                    // (left) + New-transaction button & keyboard hint
                    // (right), one row. Identical to bank.
                    <RegisterControlsBar
                        hideNew={!canEdit}
                        statusFilter={statusFilter}
                        onStatusFilterChange={setStatusFilter}
                        sort={sort}
                        onSortChange={setSort}
                        isInvestment={isInvestment}
                        filter={filter}
                        onFilterChange={setFilter}
                        // An investment account faces categories, like any
                        // other money account; the picker applies eligibility
                        // itself, so it wants the whole list.
                        accounts={accountsQuery.data ?? []}
                        // The amount range is in the brokerage's currency (ADR-0114 D14).
                        currency={currency}
                        tags={tagsQuery.data ?? []}
                        securities={securitiesQuery.data ?? []}
                        resultCount={filterResultCount}
                        statusCounts={statusCounts}
                        onNew={startCreate}
                        // Matches the bank register: an open row editor does not
                        // block starting a new transaction. Investment disabled the
                        // button instead, which reads as broken — a greyed control
                        // with no explanation of what to close first. Bank's
                        // startCreate closes the open editor and opens the new-row
                        // form, which is what the click plainly means. Disabled
                        // too until the account's currency is known, as in the
                        // bank register: the editor prints figures in it.
                        newDisabled={isCreatingNew || currency === null}
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
                        selectAllLabel={REGISTER_SELECT_ALL_LABEL}
                    />
                    <span role="columnheader" className="truncate">Date</span>
                    <span role="columnheader" className="truncate">Action · Check #</span>
                    <span role="columnheader" className="truncate">Payee · Memo</span>
                    {/* The "|" announces the three account sub-slots; tags join
                        them with a "·" the way the bank header reads
                        "Category · tags", since they are a fourth thing in the
                        same cell rather than a fourth account. */}
                    <span role="columnheader" className="truncate">Category | Transfer · Fee · Tags</span>
                    <span role="columnheader" className="truncate">Security · Shares @ Price</span>
                    <span role="columnheader" className="truncate text-right">Amount</span>
                    <span role="columnheader" className="truncate text-right">Balance</span>
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
                            renderRow={(index, row) => {
                                // The list renders only once the currency is
                                // known (the shell's loading state holds until
                                // then), so this is never taken.
                                if (currency === null) return null;
                                // Every row here is flat: the server collapses
                                // an event into one row, and a split's lines
                                // seen from here are each their own.
                                if (row.kind !== 'txn') return null;
                                const txn = row.txn;
                                // Read-only rows can share a headerId (the
                                // lines of one split), so identity for focus +
                                // edit-substitution comes off `txn.id` there,
                                // or a click on one would select / open all
                                // its siblings.
                                const isReadOnly = txn.isReadOnly;
                                if (
                                    !isReadOnly
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
                                                inherited: editingContext.inherited,
                                                onSaved: (entries, mergedIntoHeaderId) => {
                                                    setEditingHeaderId(null);
                                                    // MERGE. The edited row is
                                                    // the loser and `entries` are
                                                    // the survivor's, so the
                                                    // ordinary path below —
                                                    // which patches the saved
                                                    // entry onto the EDITED
                                                    // header — would paint the
                                                    // survivor's data onto the
                                                    // loser's row and leave two
                                                    // entries under one header
                                                    // id (a duplicate virtuoso
                                                    // key). The server hides
                                                    // the loser, so a refresh
                                                    // heals it, which hides
                                                    // the defect. Mirrors the
                                                    // bank register.
                                                    if (mergedIntoHeaderId) {
                                                        const loserId =
                                                            editingContext.headerId;
                                                        register.replaceHeaderEntries(loserId, []);
                                                        const survivor = entries[0];
                                                        if (survivor !== undefined) {
                                                            // The survivor adopts the
                                                            // loser's posted timestamp,
                                                            // so its sort slot can move
                                                            // even within the same day.
                                                            if (reseatIfMoved(
                                                                mergedIntoHeaderId,
                                                                entryPostedAt(survivor),
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
                                                        }
                                                        register.replaceHeaderEntries(
                                                            mergedIntoHeaderId, entries,
                                                        );
                                                        // A merge REMOVES a row, so every
                                                        // running balance after it in time
                                                        // (above it on screen) moves — the
                                                        // same reason the ordinary save
                                                        // path refreshes. Skipping it left
                                                        // the balance column stale until a
                                                        // reload. (The key-moved case
                                                        // above re-seeds the window, which
                                                        // brings fresh balances with it,
                                                        // and returns before reaching here.)
                                                        // The bank register falls through
                                                        // to the same refresh.
                                                        void refreshLoadedBalances();
                                                        queryClient.invalidateQueries({
                                                            queryKey: [
                                                                'register-index-buckets',
                                                                ledgerId,
                                                                accountId,
                                                            ],
                                                        });
                                                        return;
                                                    }
                                                    // Investment PATCH is a full reshape
                                                    // per ADR-0025 — leg amounts can shift,
                                                    // every downstream balance updates.
                                                    // Swap the saved header's rows in
                                                    // place (replaceHeaderEntries with
                                                    // the server's returned entries,
                                                    // which can be several) and refresh
                                                    // downstream balances via the bulk
                                                    // balances endpoint — no full window
                                                    // re-fetch, no virtuoso data-swap,
                                                    // no scroll jerk.
                                                    const savedHeaderId =
                                                        editingContext.headerId;
                                                    const first = entries[0];
                                                    if (first !== undefined) {
                                                        // A posted-timestamp change moves
                                                        // the row's sort position (an
                                                        // Accept writes the date at
                                                        // midnight, moving a feed row
                                                        // that carried a time of day);
                                                        // re-seed the window anchored at
                                                        // it so it relocates (the re-fetch
                                                        // brings fresh balances too, so
                                                        // skip the in-place patch +
                                                        // balance refresh).
                                                        if (reseatIfMoved(
                                                            savedHeaderId, entryPostedAt(first),
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
                                                    }
                                                    register.replaceHeaderEntries(savedHeaderId, entries);
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
                                const rowFocusId = displayRowId(row);
                                return (
                                    <RegisterRow
                                        strategy={investmentRowStrategy}
                                        rowIndex={index}
                                        variant="txn"
                                        row={txn}
                                        accountPaths={accountPaths}
                                        systemCategoryIds={systemIds}
                                        currency={currency}
                                        today={today}
                                        cmdClickToggles
                                        readOnly={isReadOnly}
                                        needsReview={txn.needsReview}
                                        focused={focusedRowId === rowFocusId}
                                        onFocus={() => setFocusedRowId(rowFocusId)}
                                        onDoubleClickEdit={canEdit ? () => startEdit(txn.headerId) : undefined}
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
                                            || !canEdit
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
                                        title={canEdit
                                            ? 'Click to focus · double-click to edit · right-click for actions'
                                            : 'Click to focus · right-click for actions'}
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
                    is checked. No `extraActions` — and neither does bank
                    pass any, despite a long-standing comment here claiming
                    it had Categorize / Tag placeholders to be excluded
                    from. */}
                <RegisterBulkActionBar
                    readOnly={!canEdit}
                    selectedCount={footerCounts.selected}
                    readOnlyCount={footerCounts.readOnly}
                    selectedSum={selectedSum}
                    sumRateUnavailable={selection.summary?.rateUnavailable}
                    summaryError={selection.summaryError}
                    currency={selectedSumCurrency}
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
                    unhideDisabled={selectionHasReadOnly}
                    unhideDisabledTitle="Selection includes rows whose canonical owner is elsewhere (split counter-side). Unhide those from the source register."
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
                    items={rowActionsFor(buildRowMenuItems(contextMenu.target), canEdit)}
                    onClose={closeContextMenu}
                />
            ) : null}

            <MoveToAccountDialog
                open={recovery.moveDialogOpen}
                accounts={accountsQuery.data ?? []}
                sourceAccountId={accountId}
                // This page only ever renders for an investment account
                // (RegisterRouter), so the domain is not in doubt here — but
                // it is passed rather than assumed inside the dialog, so the
                // rule lives in one place.
                sourceAccountType="investment"
                count={selection.summary?.count ?? 0}
                pending={recovery.bulkMovePending}
                error={recovery.moveError}
                becameTwoAmount={recovery.moveBecameTwoAmount}
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
                // The refusal shows in its own dialog, which is dismissed (and
                // the refusal cleared) before a delete can be confirmed again.
                onConfirmSingle={(target) => deleteMutation.mutate(target)}
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
        </TagColorsProvider>
    );
}

/**
 * Diagnostic modal that pretty-prints the right-clicked row + its
 * legs as JSON. No styling beyond the bare minimum — this surface
 * exists for debugging (especially classifier coverage on sync
 * rows). Esc + backdrop click + close button all dismiss.
 */

/**
 * Narrow the account-scoped window to investment entries on the `kind`
 * discriminant (ADR-0030 §2), mirroring the bank register's
 * `narrowToBankEntries`. RegisterRouter only dispatches an investment account
 * here, so every row is `kind: 'investment'`; a non-investment row would be a
 * routing defect and is dropped rather than coerced.
 *
 * The server collapses each entry into one event row (ADR-0080), so this
 * register never receives a group. Were one to arrive, its rows are shown one
 * per row, as a split's lines are on their other side — never folded back
 * into a cluster, and never dropped.
 */
function narrowToInvestmentEntries(
    entries: readonly RegisterEntry[],
): RegisterEntryOf<InvestmentRowType>[] {
    const out: RegisterEntryOf<InvestmentRowType>[] = [];
    for (const entry of entries) {
        const rows = entry.kind === 'txn' ? [entry.txn] : entry.legs;
        for (const row of rows) {
            if (row.kind !== 'investment') continue;
            out.push({ kind: 'txn', txn: row, groupId: null, legs: null });
        }
    }
    return out;
}
