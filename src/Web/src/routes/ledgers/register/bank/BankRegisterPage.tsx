import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { type VirtuosoHandle } from 'react-virtuoso';

import {
    ApiError,
    bulkDeleteTransactions,
    bulkSetReconStatus,
    createTransaction,
    deleteTransaction,
    fetchAccounts,
    fetchPayees,
    fetchTags,
    fetchVisibleLedgers,
    patchTransaction,
    syncAccount,
} from '@/lib/api';
import type {
    AccountSummary,
    BankRow,
    BulkDeleteResponse,
    BulkReconStatusResponse,
    CreateTransactionRequest,
    DeleteTransactionResponse,
    LedgerSummary,
    PatchTransactionRequest,
    PayeeSuggestion,
    ReconStatus,
    RegisterEntry,
    RegisterRow as RegisterRowUnion,
} from '@/lib/types';
import { useSelection, type UseSelectionResult } from '@/lib/useSelection';
import { TxnRowEdit, patchErrorMessage } from '../../TxnRowEdit';
import type { TxnRowNewPrefill } from '../../TxnRowEdit';
import { ReminderEditorDialog } from '../../reminders/ReminderEditorDialog';
import {
    invalidateRegisterStatusCounts,
    useRegisterController,
} from '../shell/useRegisterController';
import { RegisterDeleteConfirm } from '../shell/RegisterDeleteConfirm';
import { useRegisterKeyboardNav } from '../shell/useRegisterKeyboardNav';
import { useRegisterBulkRecovery } from '../shell/useRegisterBulkRecovery';
import { buildAccountPathMap } from '@/lib/accountPath';
import { RefreshCw, Upload } from 'lucide-react';

import { ImportFileDialog } from '../shell/ImportFileDialog';
import { MoveToAccountDialog } from '../shell/MoveToAccountDialog';

import { Button } from '@/components/ui/Button';
import { ContextMenu } from '@/components/ui/ContextMenu';
import { MainArea } from '@/components/ui/SidebarLayout';

import { RegisterTopBar } from '../shell/RegisterTopBar';
import { RegisterShell } from '../shell/RegisterShell';
import { RegisterScrollSurface } from '../shell/RegisterScrollSurface';
import { RegisterScrollTrack } from '../RegisterScrollTrack';
import { RegisterDateJumpPopover } from '../RegisterDateJumpPopover';
import { RegisterControlsBar } from '../shell/RegisterControlsBar';
import { DEFAULT_SORT, type RegisterSortState } from '../shell/registerSort';
import { toSelectionStatusFilter } from '../shell/registerStatus';
import {
    isRegisterFilterActive,
    type RegisterFilterArgs,
    type RegisterStatusCounts,
} from '@/lib/api/register';
import { RegisterLeadHeaderCells } from '../shell/RegisterRowLead';
import { RegisterVirtualList } from '../shell/RegisterVirtualList';
import {
    buildDisplayRows,
    groupAmount,
    groupBalanceAfter,
    regroupTargetSplits,
    type DisplayRow,
} from '@/lib/splitCollapse';

import {
    BANK_COLS,
    isInvestmentOwnedRow,
    passesStatusFilter,
    resolveRowStatus,
    type StatusFilter,
} from './columns';
import { RegisterRow } from '../shell/RegisterRow';
import { TagColorsProvider } from '@/components/tags/TagColorsContext';
import { bankRowStrategy } from '../strategies/bankRowStrategy';
import { RegisterBulkActionBar } from '../shell/RegisterBulkActionBar';
import { buildBankRowMenuItems } from './bankRowMenu';

// Per-account register, full Moneydance feature-parity columns:
//   * Toolbar with status filters (All / Cleared / Pending / Scheduled)
//     applied client-side over the loaded transactions
//   * Selection checkboxes + bulk-action footer (Phase 6+ wires)
//   * Status column: ✓ cleared / P pending / S scheduled (future-dated)
//   * Date column with optional tax-date sub-line when transactedAt is set
//     and differs from postedAt
//   * Check# column — populated for paper-check transactions
//   * Payee · memo column (truncated)
//   * Category chip — the "other leg" of the symmetric posting per
//     ADR-0019, mapped to a chip variant via categoryChipVariant
//   * Tags chips — small default-variant chips, deterministic order
//   * Outflow / Inflow / Balance — monospace tabular-num
//
// Future-dated rows render with muted text (text-text-muted on cells)
// per the design call on 2026-05-11 — feature parity with MD's
// scheduled-entry indicator, but our own treatment (no yellow
// highlight, just de-emphasis + the S badge).

import { formatLedgerDate } from '@/lib/dates';

const PAGE_SIZE = 100;

// Row-status derivation (resolveRowStatus / isScheduled /
// taxDateSubLabel / passesStatusFilter) + the StatusFilter / RowStatus
// types + the BANK_COLS grid template live in `./columns` (a
// non-component module the row components share). Split-transaction
// collapse logic lives in `@/lib/splitCollapse`; the page renders the
// resulting DisplayRow stream below.

/**
 * Build a new-transaction prefill (Duplicate path) from one OR MORE
 * source rows. A single-row duplicate passes `[row]` → one posting; a
 * split duplicate passes all the group's legs → N postings. Same path —
 * a single row is just the N=1 case (no split special-casing). Header
 * fields come from the first row; posted_at defaults to today in the
 * editor.
 */
function rowsToDuplicatePrefill(
    rows: readonly BankRow[],
): TxnRowNewPrefill {
    const head = rows[0]!;
    return {
        payee: head.payee,
        memo: head.headerMemo,
        checkNumber: head.checkNumber,
        postings: rows.map((r) => ({
            counterpartyAccountId: r.counterpartyAccountId,
            amount: r.amount,
            legMemo: r.legMemo,
        })),
    };
}

export function BankRegisterPage() {
    const { ledgerId, accountId } = useParams({ strict: false }) as {
        ledgerId: string;
        accountId: string;
    };

    // Optional `?focus=<headerId>` search param. The "Show other side"
    // row action navigates here with focus set so the receiving page
    // scrolls + focuses the matching counterparty leg on load. NULL
    // when navigating directly (e.g. from the sidebar).
    const search = useSearch({ strict: false }) as { focus?: string };
    const focusFromUrl = search.focus;

    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    // The register works with the full account universe — including inactive
    // accounts (so an inactive account's register still resolves its name +
    // currency for the breadcrumb) and every counterparty (so the row chips +
    // editor render any account). "What's pickable as a NEW counterparty"
    // (active, non-system) is the picker's own eligibility filter
    // (pickableCounterparties), not something we enforce by starving this list.
    // Shares the includeInactive cache with the sidebar's "show inactive" query.
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
    });

    const ledger = findById<LedgerSummary>(ledgersQuery.data, ledgerId);
    const account = findById<AccountSummary>(accountsQuery.data, accountId);
    // RegisterRouter dispatches investment accounts to
    // InvestmentRegisterPage, so this page only ever renders
    // bank-shape account types (bank / credit / cash / asset /
    // liability / loan / category). The cell renderers are bank-
    // specific and imported directly from `../strategies/bankStrategy`.

    // Shared register-container controller (review #18): owns the
    // windowed register, the index-bucket query, currentYearMonth,
    // seekToBucket, refreshLoadedBalances, and the recon-status
    // optimistic patch + mutation + cycle order. Initial load anchors
    // on `focusFromUrl` when present (the Show-Other-Side arrival
    // path); otherwise returns the most-recent K entries.
    // Status filter, incl. the ADR-0072 Hidden view. Declared here —
    // above the controller — because `hidden` re-scopes the windowed
    // fetch, so the controller takes it as an input.
    const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
    // Structured/search filter (mig 164). useState keeps a stable reference
    // between renders — required, since the controller threads it into the
    // windowed hook's effect deps.
    const [filter, setFilter] = useState<RegisterFilterArgs>({});
    // Column sort (mig 166). Threaded to the controller (windowed read only)
    // and the controls bar's Sort ▾ menu. A bank register offers no
    // investment-only columns. A change resets the window (fresh keyset walk).
    const [sort, setSort] = useState<RegisterSortState>(DEFAULT_SORT);

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
        focusHeaderId: focusFromUrl,
        pageSize: PAGE_SIZE,
        statusFilter,
        filter,
        sort,
    });

    // Total matches for the filter chips row — the (filter-aware) scroll-track
    // buckets sum to the full filtered entry count. Shown only when a
    // structured/search dimension is active.
    const filterActive = isRegisterFilterActive(filter);
    const filterResultCount = filterActive
        ? indexBuckets.reduce((sum, b) => sum + b.count, 0)
        : null;

    // Pre-fetch the payee suggestions for the inline-edit typeahead.
    // Server ranks them; the SPA filters in-memory on every keystroke
    // (a personal-finance ledger has O(hundreds) of distinct payees).
    const payeesQuery = useQuery({
        queryKey: ['payees', ledgerId],
        queryFn: () => fetchPayees(ledgerId),
        staleTime: 30_000,
    });

    // PATCH a transaction's overrides (header fields + leg edits)
    // in one atomic call. Under the sliding-window model the
    // register isn't a TanStack Query — `register.mutateEntries`
    // drives optimistic patches directly. We still use
    // `useQueryClient` for the payees-typeahead cache (separate
    // useQuery), which the register doesn't own.
    const navigate = useNavigate();

    // Clear the ?focus= anchor whenever the structured/search filter changes
    // (ADR-0076). A pinned focus row must not survive a freshly-applied filter
    // and hijack the top of the filtered list. The backend also refuses to pin a
    // non-matching anchor (ResolveCursorForHeaderAsync), but dropping the URL
    // anchor here gives a clean filtered list from the top even when the anchor
    // would still match.
    const handleFilterChange = useCallback(
        (next: RegisterFilterArgs) => {
            setFilter(next);
            if (focusFromUrl !== undefined) {
                void navigate({
                    to: '/ledgers/$ledgerId/accounts/$accountId',
                    params: { ledgerId, accountId },
                    search: {},
                } as unknown as Parameters<typeof navigate>[0]);
            }
        },
        [focusFromUrl, navigate, ledgerId, accountId],
    );

    const queryClient = useQueryClient();
    const patchMutation = useMutation({
        mutationFn: (args: { headerId: string; body: PatchTransactionRequest }) =>
            patchTransaction(ledgerId, args.headerId, args.body, accountId),
        onSuccess: (savedEntry, args) => {
            queryClient.invalidateQueries({ queryKey: ['payees', ledgerId] });
            // Any save can clear / drop a `needs_review` row: an
            // edit-then-approve (approve: true) clears the flag, and a
            // merge stamps `is_merged_into` on the source row (dropping
            // it from the account). The sidebar's green review-dot
            // reads `needsReviewCount` off the per-ledger accounts
            // query, so invalidate it here — the dot resets live
            // (lightweight background refetch, no page reload) when the
            // last pending item in the account is handled.
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            // Slice 2c.6 follow-up: any save can shift the Accept-
            // flow chip surfaces — accepting a row clears its
            // needs_review (the editor's gates now return empty);
            // a merge stamps `is_merged_into` on the source row,
            // dropping it from every OTHER row's candidate list;
            // approving a manual row adds a new (payee, category)
            // datapoint that may surface as a similar-payee
            // suggestion elsewhere. Invalidate the per-ledger
            // family so the next editor open re-fetches; the
            // queries are cheap and per-row, so the cost is bounded
            // by how many rows the user opens, not how many exist.
            queryClient.invalidateQueries({ queryKey: ['merge-candidates', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['similar-payees', ledgerId] });

            // Always patch the saved row in place from the server's
            // response (or drop it if the row no longer matches the
            // view's visibility filters — e.g. it became hidden /
            // merged-away). For BALANCE-AFFECTING edits we ALSO
            // fetch fresh balance + net-amount for every header
            // currently loaded and patch them in place — same
            // refresh path the user sees on a manual reload, just
            // scoped to the loaded window so virtuoso doesn't
            // data-swap and the scroll position survives.
            //
            // Previously this branch called `register.refresh(headerId)`
            // which re-fetched the whole page, swapped virtuoso's
            // data prop, and jerked the viewport. The in-place
            // balance refresh below preserves the row's screen
            // position — important after the user spends seconds in
            // an edit and expects to land back where they were.
            const savedHeaderId = args.headerId;
            const mergedIntoId = args.body.mergeFromHeaderId ?? null;
            if (mergedIntoId !== null) {
                // Inverted-merge direction: the editor row
                // (savedHeaderId) became the loser; the candidate
                // (mergedIntoId) is the surviving canonical row.
                // The server returned the survivor's freshly-
                // resolved entry, so swap THAT into place (it may
                // have a refreshed balance / is_merge_winner flag),
                // and remove the editor row from the view.
                register.removeEntries((entry) =>
                    (entry.kind === 'txn' ? entry.txn.headerId : entry.legs[0]!.headerId)
                        === savedHeaderId);
                if (savedEntry !== null) {
                    // ADR-0034 / #324: the survivor adopts the imported (loser)
                    // row's posted date, which can move its sort position. Re-seed
                    // anchored at the survivor — the same reposition the date-edit
                    // branch below does — so it lands in its new date-sorted slot
                    // (the re-fetch also brings fresh running balances). Without
                    // this the merged row is stuck at its OLD slot until a manual
                    // refresh. When the date did NOT move, fall through to the
                    // in-place swap that keeps the viewport steady.
                    const survivorPostedAt =
                        savedEntry.kind === 'txn'
                            ? savedEntry.txn.postedAt
                            : savedEntry.legs[0]!.postedAt;
                    if (repositionIfDateChanged(mergedIntoId, survivorPostedAt)) {
                        queryClient.invalidateQueries({
                            queryKey: ['register-index-buckets', ledgerId, accountId],
                        });
                        return;
                    }
                    register.mutateEntries((entry) => {
                        const entryHeaderId =
                            entry.kind === 'txn'
                                ? entry.txn.headerId
                                : entry.legs[0]!.headerId;
                        return entryHeaderId === mergedIntoId ? savedEntry : entry;
                    });
                }
                // Date unchanged: the survivor was swapped in-place above and is
                // focused in-place by saveEdit (setFocusedRowId on the returned
                // entry) — so the viewport stays put after a fold. Do NOT navigate
                // to ?focus=survivor here: that re-seeds the windowed register and
                // jerks the scroll on every merge (regression — merges should
                // highlight the survivor in place, not re-fetch + scroll).
            } else if (savedEntry === null) {
                register.removeEntries((entry) =>
                    (entry.kind === 'txn' ? entry.txn.headerId : entry.legs[0]!.headerId)
                        === savedHeaderId);
            } else {
                // A posted-date change moves the row's sort position, so
                // patching in place would leave it stuck. Re-seed the
                // window anchored at the header so it relocates to its
                // new date-sorted slot (the re-fetch also brings fresh
                // balances, so skip the in-place patch + balance refresh
                // below). Non-date edits fall through to the in-place
                // patch that preserves the row's screen position.
                const newPostedAt =
                    savedEntry.kind === 'txn'
                        ? savedEntry.txn.postedAt
                        : savedEntry.legs[0]!.postedAt;
                if (repositionIfDateChanged(savedHeaderId, newPostedAt)) {
                    queryClient.invalidateQueries({
                        queryKey: ['register-index-buckets', ledgerId, accountId],
                    });
                    return;
                }
                register.mutateEntries((entry) => {
                    const entryHeaderId =
                        entry.kind === 'txn'
                            ? entry.txn.headerId
                            : entry.legs[0]!.headerId;
                    return entryHeaderId === savedHeaderId ? savedEntry : entry;
                });
            }

            const isBalanceAffecting =
                args.body.mergeFromHeaderId != null
                || args.body.postings != null
                || args.body.postedAt != null;
            if (isBalanceAffecting) {
                void refreshLoadedBalances();
                queryClient.invalidateQueries({
                    queryKey: ['register-index-buckets', ledgerId, accountId],
                });
            }
            // Any edit can shift a counted dimension — needs_review via Approve,
            // or category/amount/date under an active filter — so refresh the
            // status counts. (The date + balance branches above already
            // invalidate the parent, which covers counts; this catches the
            // in-place-patch case that otherwise leaves counts stale.)
            invalidateRegisterStatusCounts(queryClient, ledgerId, accountId);
        },
    });

    // POST a new manual transaction. On success invalidate the
    // register + payees + accounts queries (new payee might create
    // a fresh entry in the typeahead source; accounts list doesn't
    // change but balances do once balance materialisation lands).
    const createMutation = useMutation({
        mutationFn: (body: CreateTransactionRequest) =>
            createTransaction(ledgerId, body),
        onSuccess: (response) => {
            queryClient.invalidateQueries({ queryKey: ['payees', ledgerId] });
            // A new manual row in the source account may match an
            // existing needs_review row's amount/date window — so a
            // future open of that row should re-fetch its candidate
            // list rather than serving stale (pre-create) data.
            queryClient.invalidateQueries({ queryKey: ['merge-candidates', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['similar-payees', ledgerId] });
            // The new row may land in a month that didn't previously
            // have a bucket — invalidate so the scroll-track shows it.
            queryClient.invalidateQueries({
                queryKey: ['register-index-buckets', ledgerId, accountId],
            });
            // Navigate to ?focus=<newHeaderId> so the Show-Other-Side
            // arrival path takes over: useWindowedRegister re-seeds
            // with starting_at=<headerId>, anchors the new row at the
            // top, and the seededFocusForIndexRef effect scrolls +
            // single-selects it. Same code path as right-click "Show
            // other side" — no parallel "scroll-to-new-row" plumbing.
            void navigate({
                to: '/ledgers/$ledgerId/accounts/$accountId',
                params: { ledgerId, accountId },
                search: { focus: response.headerId },
            } as unknown as Parameters<typeof navigate>[0]);
        },
    });

    // Slice 2c: flip the bank-feed review flag in-place so the
    // row's visual treatment clears immediately. Same trust-the-
    // mutation posture as reconStatusMutation — server-side
    // refresh handles the rare rejection case.
    const patchHeaderNeedsReview = useCallback(
        (headerId: string, needsReview: boolean) => {
            const patchTxn = <R extends RegisterRowUnion>(txn: R): R =>
                txn.headerId === headerId ? { ...txn, needsReview } : txn;
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

    // Slice 2c.6a: approve via PATCH with `approve: true`. The
    // dedicated POST /approve endpoint was retired so the typical
    // bank-feed flow (edit-then-approve) lands in one round-trip;
    // approve-as-is is still a single call with an otherwise-empty
    // body. patchTransaction returns the freshly-resolved register
    // entry when account-scoped, which the register window already
    // ignores for this lightweight mutation (optimistic patch fires
    // in onMutate).
    const approveMutation = useMutation<RegisterEntry | null, ApiError, { headerId: string }>({
        mutationFn: (args) =>
            patchTransaction(ledgerId, args.headerId, { approve: true }, accountId),
        onMutate: (args) => patchHeaderNeedsReview(args.headerId, false),
        onSuccess: () => {
            // Approve clears the row's `needs_review`. Invalidate the
            // per-ledger accounts query so the sidebar's green review-
            // dot (sourced from `needsReviewCount`) resets live when
            // this was the account's last pending item — no reload.
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
        },
    });

    // Slice 2c.3: per-account sync. Fires when the user clicks
    // the Sync icon in the register top bar. Account-scoped — only
    // the bound SimpleFIN account is pulled (not the whole
    // connection), so the wire roundtrip is small and the result
    // affects just this register. The server-side
    // SyncConnectionLock + DB UNIQUE index keep concurrent
    // per-connection syncs serialized.
    const syncAccountMutation = useMutation({
        mutationFn: () => syncAccount(ledgerId, accountId),
        onSuccess: () => {
            // Fresh rows just landed in txn_headers — refresh the
            // sliding window so the new (needs_review) entries
            // appear at their date-sorted position.
            register.refresh();
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({
                queryKey: ['register-index-buckets', ledgerId, accountId],
            });
        },
    });

    // File-import wizard (OFX/QFX/QIF — format by file extension).
    // Dialog state is local — the dialog handles preview + import
    // internally and calls `onImported` so we can refresh the window.
    const [importDialogOpen, setImportDialogOpen] = useState(false);

    // DELETE a single transaction. Bank registers may show legs
    // from investment headers (Transfer / DivXfr cash side), but
    // those rows are read-only here — the `↗ Investment` badge +
    // context-menu gate prevent the user from initiating a delete
    // on a row whose canonical owner is an investment header. So
    // every row that reaches this mutation is bank-shape, and we
    // can call the bank delete endpoint unconditionally. Server
    // picks hard-delete (manual entries, no external_id) vs
    // soft-hide (any feed / import-keyed row); response carries
    // the chosen kind so the caller can render the appropriate
    // toast / confirmation copy.
    //
    // On success: refresh the windowed register so the deleted row
    // disappears AND every downstream row's balance refetches with
    // the post-DELETE recompute applied by the API's
    // BalanceRecomputeInterceptor. Just removing the entry locally
    // would leave the rest of the window showing pre-delete balance
    // values — same bug class as patch-without-refresh.
    // Also invalidate the holdings + accounts queries since deletes
    // can shift cost-basis + review-dot counts.
    const deleteMutation = useMutation<
        DeleteTransactionResponse,
        ApiError,
        BankRow
    >({
        mutationFn: (target) => deleteTransaction(ledgerId, target.headerId),
        onSuccess: (_response, target) => {
            // In-place delete: drop the row from the loaded window,
            // then refresh balance + net-amount on every remaining
            // entry (the server's recompute has already shifted
            // downstream values). No full re-fetch, no virtuoso
            // data-swap, no scroll jump.
            const deletedId = target.headerId;
            register.removeEntries((entry) =>
                (entry.kind === 'txn' ? entry.txn.headerId : entry.legs[0]!.headerId)
                    === deletedId);
            void refreshLoadedBalances();
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['holdings'] });
            queryClient.invalidateQueries({
                queryKey: ['register-index-buckets', ledgerId, accountId],
            });
        },
    });

    // Bulk recon-status mutation (ADR-0024). Sends the active
    // selection to the server in one round-trip — server resolves
    // the predicate and applies the status in one atomic UPDATE.
    // Optimistic-update for VISIBLE rows only: the SPA enumerates
    // currently-loaded headers matching the selection and flips their
    // badge before the round-trip resolves. Rows not in memory will
    // surface the new status when they next re-enter the window.
    const bulkReconStatusMutation = useMutation<
        BulkReconStatusResponse,
        ApiError,
        ReconStatus
    >({
        mutationFn: (status) =>
            bulkSetReconStatus(ledgerId, selection.selection, accountId, status),
        onMutate: (status) => {
            // Optimistic: walk currently-loaded entries, find every
            // header in the active selection (regardless of whether
            // it's visible after filtering — the predicate covers
            // them), flip their status badge.
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
            // Clear selection — the bulk intent has been fulfilled,
            // and leaving N rows checked across stale predicate
            // semantics is the kind of dangling state ADR-0024
            // explicitly avoids.
            selection.clear();
            // Bulk recon-status shifts rows between status views; refresh the
            // dropdown counts (month totals unchanged, so counts-only).
            invalidateRegisterStatusCounts(queryClient, ledgerId, accountId);
        },
    });

    // Bulk delete mutation (ADR-0024). Mirrors the single-row delete
    // policy (hard-vs-soft based on external_id) but applied across
    // the entire selection in one server-side transaction. The
    // server-side BulkDeleteAsync explicitly invokes the balance
    // recompute service after its ExecuteDeleteAsync (which bypasses
    // the EF SaveChangesInterceptor); we refresh the window so the
    // updated balance rows surface for every remaining row.
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
        },
    });

    // Account path map for the counterparty Typeahead — memoised so
    // we don't rebuild on every keystroke in the edit row.
    const accountPaths = useMemo(
        () => buildAccountPathMap(accountsQuery.data ?? []),
        [accountsQuery.data],
    );

    // `entries` from useWindowedRegister is already the flat
    // time-DESC array — no flatMap/page-walk needed under the
    // sliding-window model.
    //
    // ADR-0030 §2: the register read is account-scoped, so a bank
    // account's window is homogeneous `BankRow`. Narrow on the `kind`
    // discriminant once at the page boundary (RegisterRouter dispatches
    // investment accounts to InvestmentRegisterPage, so any non-`bank`
    // row here would be a routing bug — drop it rather than coerce).
    // Everything downstream then operates on `BankRow` with no casts.
    // Cluster ADR-0036 target-splits into expandable parents — the shared
    // regroup (ADR-0080), the same one the investment register uses.
    const allEntries = useMemo<readonly BankEntry[]>(
        () => regroupTargetSplits<BankRow>(narrowToBankEntries(register.entries)),
        [register.entries],
    );

    // Bulk-selection state (ADR-0024). useSelection owns the
    // discriminated explicit/all selection + a debounced server-side
    // summary query (count, Σ). It supersedes the old leg-id Set —
    // selection now lives in HEADER id space because bulk actions
    // operate on headers.
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

    // Bulk recovery (Unhide / Move, ADR-0072 D2/D3) — shared with the
    // investment register via useRegisterBulkRecovery so the two can't drift.
    const recovery = useRegisterBulkRecovery({
        ledgerId,
        accountId,
        selection,
        onRefresh: () => register.refresh(),
    });

    const [expandedGroups, setExpandedGroups] = useState<ReadonlySet<string>>(
        () => new Set(),
    );

    // Pin "today" once per render so filter behavior is stable for
    // the duration of a render pass. Day-level resolution.
    const today = useMemo(() => new Date(), []);

    // Status is derived from the entry's canonical row — for a group,
    // that's the leg_index=0 leg (server sorts ASC so legs[0]). Every
    // leg in a group shares posted_at and (typically) feed_status, so
    // the canonical leg is representative.
    const entryStatusRow = (entry: BankEntry): BankRow =>
        entry.kind === 'txn' ? entry.txn : entry.legs[0]!;

    const visibleEntries = useMemo(
        () =>
            allEntries.filter((e) =>
                passesStatusFilter(entryStatusRow(e), statusFilter, today),
            ),
        [allEntries, statusFilter, today],
    );

    // Bank / credit / cash / asset / liability entries pass straight
    // through to buildDisplayRows, where multi-leg groups keep the
    // split-parent / expand-children flow (legit for paychecks etc.).
    const displayRows = useMemo(
        () => buildDisplayRows<BankRow>(visibleEntries, expandedGroups),
        [visibleEntries, expandedGroups],
    );

    /** User-facing "row count" — count of register entries (not raw
     *  transactions). A 14-leg paycheck is ONE entry, not 14 rows. */
    const visibleEntryCount = visibleEntries.length;

    const currency = account?.currencyCode ?? 'USD';

    function toggleGroupExpanded(groupId: string) {
        setExpandedGroups((prev) => {
            const next = new Set(prev);
            if (next.has(groupId)) next.delete(groupId);
            else next.add(groupId);
            return next;
        });
    }

    return (
        <MainArea>
            <RegisterTopBar
                ledgerId={ledgerId}
                ledger={ledger ?? null}
                accountName={account?.name ?? null}
                actions={
                    <>
                        {/* Statement-file import (OFX/QFX — ADR-0031 Phase 4;
                            QIF — ADR-0042). Only for an active real account —
                            never a category (you don't import a statement into a
                            budget category) nor an inactive account. */}
                        {account?.isActive && account?.accountType !== 'category' ? (
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                title="Import statement file (OFX / QFX / QIF)"
                                onClick={() => setImportDialogOpen(true)}
                                className="ml-2 gap-1.5"
                            >
                                <Upload className="h-3.5 w-3.5" aria-hidden />
                                Import
                            </Button>
                        ) : null}
                        {account?.isActive
                        && account?.feedConnectionId !== null
                        && account?.feedConnectionId !== undefined ? (
                            // Slice 2c.3: per-account sync. Visible only when
                            // this account is bound to a SimpleFIN connection;
                            // pulls JUST this account (the connection-wide sync
                            // lives on the Bank feeds settings).
                            <Button
                                type="button"
                                variant="secondary"
                                size="sm"
                                title="Sync this account from SimpleFIN"
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
                        // Same refresh path as a SimpleFIN sync —
                        // freshly-landed rows need the windowed
                        // register to re-fetch.
                        register.refresh();
                        queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
                        queryClient.invalidateQueries({
                            queryKey: ['register-index-buckets', ledgerId, accountId],
                        });
                    }}
                />
            ) : null}

            {/* The register is a workflow surface — the TABLE is the
                single scrollable region. We do NOT wrap the rest in
                MainPane (which has its own overflow-y-auto) because
                that produces a double scrollbar (one for the main
                pane, one inside the virtualizer). Instead, the KPI
                strip + Toolbar are intrinsic-height siblings, and
                the table grows into the remaining space with its
                own scroll container. */}
            <div className="flex flex-1 flex-col overflow-hidden">
                {/* No account-summary band here: the account name lives in
                    the breadcrumb, the Uncleared / Scheduled counts live on
                    the status-filter tabs (in the controls bar below), and
                    row amounts already carry currency formatting — a KPI
                    strip would only duplicate them. The table starts right
                    under the breadcrumb + tabs. */}

                    <RegisterTable
                        ledgerId={ledgerId}
                        displayRows={displayRows}
                        rowCount={visibleEntryCount}
                        currency={currency}
                        today={today}
                        selection={selection}
                        statusFilter={statusFilter}
                        onStatusFilterChange={setStatusFilter}
                        sort={sort}
                        onSortChange={setSort}
                        isInvestment={false}
                        onToggleGroupExpanded={toggleGroupExpanded}
                        onLoadOlder={register.loadOlder}
                        onLoadNewer={register.loadNewer}
                        loadingOlder={register.loadingOlder}
                        loadingNewer={register.loadingNewer}
                        focusIndex={register.focusIndex}
                        firstItemIndex={register.firstItemIndex}
                        atTimelineHead={register.atTimelineHead}
                        atTimelineTail={register.atTimelineTail}
                        oldestEntry={register.entries.at(-1) ?? null}
                        payees={payeesQuery.data ?? []}
                        accounts={accountsQuery.data ?? []}
                        accountPaths={accountPaths}
                        currentAccountId={accountId}
                        onPatch={(headerId, body) =>
                            patchMutation.mutateAsync({ headerId, body })
                        }
                        isPatching={patchMutation.isPending}
                        onCreate={(body) => createMutation.mutateAsync(body)}
                        isCreating={createMutation.isPending}
                        onCycleReconStatus={cycleReconStatus}
                        onApprove={(headerId) =>
                            approveMutation.mutate({ headerId })
                        }
                        onDelete={(target) => deleteMutation.mutateAsync(target)}
                        onBulkSetReconStatus={(status) =>
                            bulkReconStatusMutation.mutate(status)
                        }
                        onBulkDelete={() => bulkDeleteMutation.mutate()}
                        isBulkDeleting={bulkDeleteMutation.isPending}
                        onBulkUnhide={recovery.onBulkUnhide}
                        bulkUnhidePending={recovery.bulkUnhidePending}
                        onOpenMoveDialog={recovery.openMoveDialog}
                        onShowOtherSide={(counterpartyAccountId, headerId) => {
                            // Navigate to the counterparty account's
                            // register with ?focus=<headerId> so the
                            // receiving page scrolls + focuses that
                            // row. Cast escapes the router's path-param
                            // typing (registered routes only know
                            // /ledgers/$ledgerId/accounts/$accountId
                            // shapes, but the search-param widening
                            // isn't inferred at this call site).
                            void navigate({
                                to: '/ledgers/$ledgerId/accounts/$accountId',
                                params: {
                                    ledgerId,
                                    accountId: counterpartyAccountId,
                                },
                                search: { focus: headerId },
                            } as unknown as Parameters<typeof navigate>[0]);
                        }}
                        indexBuckets={indexBuckets}
                        currentYearMonth={currentYearMonth}
                        onSeekBucket={seekToBucket}
                        filter={filter}
                        onFilterChange={handleFilterChange}
                        filterResultCount={filterResultCount}
                        statusCounts={statusCounts}
                        initialLoaded={register.initialLoaded}
                        initialError={register.initialError}
                        isEmpty={allEntries.length === 0}
                        filterActive={filterActive}
                    />
            </div>
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
        </MainArea>
    );
}

interface RegisterTableProps {
    /** Ledger scope. Threaded down to TxnRowEdit so the editor's
     *  similar-payees fetch (slice 2c.6c) has a per-ledger key. */
    ledgerId: string;
    displayRows: readonly DisplayRow<BankRow>[];
    /** Underlying transaction count (after filter, before split-collapse).
     *  Used for the "N rows loaded" footer copy — users care about the
     *  transaction count, not the post-collapse row count. */
    rowCount: number;
    currency: string;
    today: Date;
    /** Bulk-selection facade (ADR-0024). Owns the discriminated
     *  explicit-vs-all selection state and exposes per-row +
     *  per-visible-set predicates. RegisterTable reads these to
     *  render checkboxes; bulk-action buttons fire the parent's
     *  bulk mutations which read `selection.selection` to send to
     *  the server. */
    selection: UseSelectionResult;
    /** Active status-filter tab — rendered in the combined controls
     *  bar inside RegisterShell (fold #4). */
    statusFilter: StatusFilter;
    /** Change the active status-filter tab. */
    onStatusFilterChange: (next: StatusFilter) => void;
    /** Column sort (mig 166) + handler. Bank never offers investment columns. */
    sort: RegisterSortState;
    onSortChange: (next: RegisterSortState) => void;
    isInvestment: boolean;
    /** Structured/search filter (mig 164) + its handler + result count,
     *  rendered in the controls bar. */
    filter: RegisterFilterArgs;
    onFilterChange: (next: RegisterFilterArgs) => void;
    filterResultCount: number | null;
    /** Per-status counts for the Show dropdown's badges (mig 165). */
    statusCounts: RegisterStatusCounts | null;
    /** List-area gating forwarded to RegisterShell so the toolbar (search /
     *  filter controls) stays mounted across filter-triggered refetches. */
    initialLoaded: boolean;
    initialError: unknown;
    isEmpty: boolean;
    filterActive: boolean;
    /** Count badges for the Uncleared / Scheduled / Needs review tabs. */
    onToggleGroupExpanded: (groupId: string) => void;
    /** Load more older entries (called when virtuoso reaches the
     *  bottom of the visible list). No-op when there's no older
     *  history left. */
    onLoadOlder: () => void;
    /** Load more newer entries (called when virtuoso reaches the
     *  top of the visible list). No-op at the timeline head. */
    onLoadNewer: () => void;
    /** True while a load-older fetch is in flight. */
    loadingOlder: boolean;
    /** True while a load-newer fetch is in flight. */
    loadingNewer: boolean;
    /** LOGICAL index of the focused row (from `?focus` URL param).
     *  -1 when no focus. Logical = stable across hook evictions:
     *  consumers add `firstItemIndex` to data-array indices and
     *  subtract it before reading from data. Passed to virtuoso's
     *  `initialTopMostItemIndex` to land on the row on mount. */
    focusIndex: number;
    /** Logical index of `displayRows[0]`. Bumps up on hook eviction
     *  from the newer edge; bumps down on prepend. Wired to
     *  virtuoso's `firstItemIndex` so scroll position survives
     *  array mutations. */
    firstItemIndex: number;
    /** True when the window covers the absolute timeline head —
     *  drives the "Newest transaction" sentinel above the first
     *  row. Honest cue that no more rows exist past the top. */
    atTimelineHead: boolean;
    /** True when the window covers the absolute timeline tail —
     *  drives the "Oldest transaction" sentinel below the last row. */
    atTimelineTail: boolean;
    /** Oldest entry currently loaded (the tail). Used to label the
     *  tail sentinel with a real date so the user has a date anchor
     *  at the timeline end. Null only on the empty pre-fetch state. */
    oldestEntry: RegisterEntry | null;
    /** Pre-fetched payee suggestions for the edit form's typeahead. */
    payees: readonly PayeeSuggestion[];
    /** All accounts in the ledger — drives the counterparty
     *  Typeahead in new-transaction mode (and slice #2's category
     *  editing on existing rows). */
    accounts: readonly AccountSummary[];
    /** Pre-built slash-joined paths keyed by account id. */
    accountPaths: Map<string, string>;
    /** Current register's account id (source leg for new txns). */
    currentAccountId: string;
    /** Apply the patch to a single transaction. Returns the mutation
     *  promise so the edit row can exit edit mode only after the
     *  server confirms (or surface errors on failure). */
    onPatch: (headerId: string, body: PatchTransactionRequest) => Promise<RegisterEntry | null>;
    /** True while a save is in flight — disables Save and the input
     *  cells in the edit row so a rapid second commit doesn't race
     *  the first. */
    isPatching: boolean;
    /** Create a new manual single-row transaction. Returns the
     *  mutation promise so the new-row form can wait for the server
     *  to confirm before exiting create mode. */
    onCreate: (body: CreateTransactionRequest) => Promise<unknown>;
    isCreating: boolean;
    /** Cycle the reconciliation status on one header. Called from
     *  the status-badge click; ordering is uncleared → reconciling →
     *  cleared → uncleared. */
    onCycleReconStatus: (headerId: string, current: ReconStatus) => void;
    /** Delete a transaction. Server picks hard-vs-soft based on the
     *  row's external_id; the returned promise carries the chosen
     *  kind so the caller can render the right toast. */
    /** Delete a bank-register row. Every row here is `BankRow`
     *  (the page is account-scoped to a bank-domain account), and
     *  read-only investment-owned / split-counter rows are gated
     *  out of the delete affordance upstream, so this always hits
     *  the bank delete endpoint. Returns the server's chosen kind
     *  (hard-deleted vs soft-hidden) for toast copy. */
    onDelete: (target: BankRow) => Promise<DeleteTransactionResponse>;
    /** Navigate to the counterparty account's register, optionally
     *  with `?focus=<headerId>` so the receiving page scrolls to and
     *  focuses that row on load. */
    onShowOtherSide: (counterpartyAccountId: string, headerId: string) => void;
    /** Set the reconciliation status on the active selection
     *  (ADR-0024). Server resolves the predicate; SPA optimistically
     *  flips visible badges. */
    onBulkSetReconStatus: (status: ReconStatus) => void;
    /** Delete the active selection (ADR-0024). Server applies the
     *  per-row hard-delete vs soft-hide policy in one transaction.
     *  Caller surfaces the typed-confirm dialog before invoking
     *  when the count is large. */
    onBulkDelete: () => void;
    /** True while a bulk delete is in flight — disables the Delete
     *  button and the confirm dialog's Confirm action so a double-
     *  click doesn't re-fire the operation. */
    isBulkDeleting: boolean;
    /** ADR-0072 D2: bulk-unhide the (hidden) selection. */
    onBulkUnhide: () => void;
    bulkUnhidePending: boolean;
    /** ADR-0072 D3: open the move-to-account dialog for the selection. */
    onOpenMoveDialog: () => void;
    /** Slice 2c: clear the bank-feed `needs_review` flag on one
     *  row. Caller wires this to the approve mutation
     *  (PATCH /transactions/{id} with `approve: true` per slice
     *  2c.6a); the row's visual treatment clears optimistically
     *  before the PATCH round-trip resolves. */
    onApprove: (headerId: string) => void;
    /** Month buckets for the date-aware scroll-track (ADR-0024
     *  follow-up). One row per month-with-activity on this account,
     *  most-recent first. Empty array → track + popover both
     *  self-hide. */
    indexBuckets: readonly import('@/lib/types/register').IndexBucketDto[];
    /** `yyyy-MM` of the topmost loaded entry — drives the
     *  scroll-track's active-bucket highlight + the "you are here"
     *  pill. Null when no entries are loaded. */
    currentYearMonth: string | null;
    /** Seek the windowed register to that bucket's anchor — called
     *  from clicks on the scroll-track AND from the Cmd/Ctrl+J
     *  date-jump popover. Argument is the bucket's `sampleHeaderId`;
     *  the parent calls `register.refresh(headerId)` to re-seed the
     *  window. */
    onSeekBucket: (sampleHeaderId: string) => void;
}

function RegisterTable({
    ledgerId,
    displayRows,
    rowCount,
    currency,
    today,
    selection,
    statusFilter,
    onStatusFilterChange,
    sort,
    onSortChange,
    isInvestment,
    filter,
    onFilterChange,
    filterResultCount,
    statusCounts,
    initialLoaded,
    initialError,
    isEmpty,
    filterActive,
    onToggleGroupExpanded,
    onLoadOlder,
    onLoadNewer,
    loadingOlder,
    loadingNewer,
    focusIndex,
    firstItemIndex,
    atTimelineHead,
    atTimelineTail,
    oldestEntry,
    payees,
    accounts,
    accountPaths,
    currentAccountId,
    onPatch,
    isPatching,
    onCreate,
    isCreating,
    onCycleReconStatus,
    onDelete,
    onShowOtherSide,
    onBulkSetReconStatus,
    onBulkDelete,
    isBulkDeleting,
    onBulkUnhide,
    bulkUnhidePending,
    onOpenMoveDialog,
    onApprove,
    indexBuckets,
    currentYearMonth,
    onSeekBucket,
}: RegisterTableProps) {
    // Ledger tag dictionary — feeds the Tag filter's autocomplete. The row
    // chips' colours come from TagColorsProvider (wraps the shell below),
    // which shares this ['tags', ledgerId] cache key so one fetch serves both.
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
        staleTime: 60_000,
    });

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

    // Which header (if any) is in row-level edit mode. The edit row
    // shows ALL editable fields at once with Save / Cancel — per-cell
    // commit-on-blur turned out to be a UX dead-end on review.
    const [editingHeaderId, setEditingHeaderId] = useState<string | null>(null);
    // Whether a new-transaction row is being composed. Mutually
    // exclusive with editingHeaderId — entering one cancels the
    // other.
    const [isCreatingNew, setIsCreatingNew] = useState(false);
    // Cursor / focused row — distinct from selection (per
    // ADR-0023 §B selection-vs-focus model). Click on a row body
    // sets focus; arrow keys move focus; Enter on focused row
    // opens edit. Selection (checkboxes) is unaffected by focus
    // changes, and vice versa.
    const [saveError, setSaveError] = useState<string | null>(null);
    // Scroll container ref. virtuoso owns the actual list element
    // but reads its scroll offset from this parent so the sticky
    // toolbar + column header (siblings of the list, inside the
    // same scroll surface) stay pinned via plain `position: sticky`.
    // State (not ref) so virtuoso re-renders once the scroll parent
    // is in the DOM (a ref leaves `customScrollParent` undefined on
    // first paint, yielding two stacked scrollbars).
    const [scrollParent, setScrollParent] = useState<HTMLDivElement | null>(null);
    const virtuosoRef = useRef<VirtuosoHandle | null>(null);

    // Keyboard navigation + focus cursor (review #18 — shared with
    // the investment register). Owns `focusedRowId`, the synchronous
    // ref-mirror, `moveFocus`, and the document ArrowUp/Down/Enter/N
    // handler. Cursor is distinct from selection (ADR-0023 §B).
    const { focusedRowId, setFocusedRowId } = useRegisterKeyboardNav<DisplayRow<BankRow>>({
        rows: displayRows,
        getRowId: displayRowId,
        onLoadNewer,
        onLoadOlder,
        // Filtering is now server-side (mig 164) — the payload IS the filtered
        // set, so edge-load always paginates through matches (never suppressed).
        suppressEdgeLoad: false,
        // Index must be LOGICAL — virtuoso adds its own firstItemIndex
        // offset internally, so we feed it firstItemIndex + localIndex.
        scrollRowIntoView: (localIndex) =>
            virtuosoRef.current?.scrollIntoView({
                index: firstItemIndex + localIndex,
            }),
        enabled: editingHeaderId === null && !isCreatingNew,
        onEnterRow: (currentId, e) => {
            // Only `txn` rows are editable in slice #1 (single-row
            // edit). Split rows are focusable but Enter is a no-op
            // until the split-edit slice lands — so we leave the
            // default intact (no preventDefault) on those, matching
            // the pre-extraction behavior.
            const row = displayRows.find(
                (r) => r.kind === 'txn' && r.txn.id === currentId,
            );
            if (row?.kind === 'txn') {
                e.preventDefault();
                startEdit(row.txn.headerId);
            }
        },
        onCreate: startCreate,
    });
    // Viewport-center `yyyy-MM` for the date-aware scroll-track's
    // "you are here" indicator. Updated by virtuoso's rangeChanged
    // (see Virtuoso prop below). Null until virtuoso emits a range
    // — the scroll-track falls back to the parent's
    // `currentYearMonth` (entries[0]-derived) in that gap.
    const [viewportYearMonth, setViewportYearMonth] = useState<string | null>(null);

    // Right-click context menu. `target` is the row the menu acts on
    // (anchored to the cursor coordinates at right-click time).
    // Closed when null.
    const [contextMenu, setContextMenu] = useState<{
        anchor: { x: number; y: number };
        target: BankRow;
        /** True when opened on a multi-leg split-parent (originating
         *  side) — the menu offers the editable-origin actions, not the
         *  read-only counter-side set. */
        originatingSplit?: boolean;
        /** All legs of the split, when opened on a split-parent — so
         *  Duplicate can clone the whole split (every posting), not just
         *  the canonical leg. */
        splitLegs?: readonly BankRow[];
    } | null>(null);
    const closeContextMenu = useCallback(() => setContextMenu(null), []);

    // Duplicate prefill — when set, the new-transaction form opens
    // prefilled with the source's header fields + postings (posted_at
    // still defaults to today). One unified shape: a single-row
    // duplicate carries one posting, a split duplicate carries all its
    // legs (see `rowsToDuplicatePrefill`). Cleared on cancel / save so
    // the next "+ New" starts blank.
    const [duplicateSource, setDuplicateSource] =
        useState<TxnRowNewPrefill | null>(null);

    // ADR-0051 slice C: "Create reminder" from a row opens the reminder editor
    // prefilled (source = this account, postings from the row[s]); the schedule
    // is left blank. Reuses the Duplicate row->prefill mapping.
    const [reminderFrom, setReminderFrom] =
        useState<{ sourceAccountId: string; prefill: TxnRowNewPrefill } | null>(null);

    // Pending destructive confirm. `null` while idle; set to either
    // a single-row delete or a bulk delete while the ConfirmDialog
    // is open. The dialog drives the actual delete on confirm.
    const [pendingDelete, setPendingDelete] = useState<
        | { kind: 'single'; target: BankRow }
        | { kind: 'bulk' }
        | null
    >(null);

    // Resolve selected leg ids → the owning header set + the sum of
    // their amounts. Multi-selected legs from the same header
    // collapse to one header (bulk reconcile / delete is header-
    // level).
    //
    // Two passes:
    //  1) Walk displayRows to collect every selected row's owning
    //     header id (txn → own header; split-parent → group's
    //     header; split-leg → leg's header).
    //
    // Post-ADR-0024 the count + Σ no longer come from this client-
    // side enumeration — `selection.summary` is the server-resolved
    // truth (works for 'all'-mode and for explicit selections whose
    // rows have been evicted from the window). The optimistic
    // update for bulk-recon-status walks `register.entries` in the
    // parent component, which has the canonical row data; no need
    // to keep a parallel visible-set here.

    /** Footer count/Σ. Defers to the server summary when present;
     *  falls back to the explicit ids count while the debounced
     *  summary query is still in flight (so the footer doesn't
     *  blink 0 on the first checkbox click). Σ uses the same
     *  fallback. */
    const selectedCount =
        selection.summary?.count ??
        (selection.selection.kind === 'explicit'
            ? selection.selection.headerIds.length
            : 0);
    const selectedSum =
        selection.summary?.sumOnAccount ?? 0;

    /** True when the user's selection includes any row that's
     *  read-only from this register's perspective (investment-owned,
     *  or counter-side of a multi-posting split). Bulk Delete sends
     *  the selection to `/transactions/bulk-delete` (bank-shape only);
     *  read-only rows must be excluded to avoid silent cross-domain
     *  mutations OR a server-side 422 mid-batch. Bulk recon-status
     *  stays enabled — it's a universal header field.
     *
     *  Only meaningful for 'explicit' selections (returns false in
     *  'all'-mode): in 'all'-mode the server restricts bulk ops to
     *  headers this account ORIGINATES (ADR-0036), so unloaded
     *  target-split rows are excluded server-side and there's nothing
     *  for the client to audit. */
    const selectionHasReadOnly = useMemo(() => {
        if (selection.selection.kind !== 'explicit') return false;
        const selectedIds = new Set(selection.selection.headerIds);
        for (const row of displayRows) {
            if (row.kind !== 'txn') continue;
            if (!selectedIds.has(row.txn.headerId)) continue;
            const inv = isInvestmentOwnedRow(row.txn);
            const split = !inv && row.txn.txnGroupId !== null;
            if (inv || split) return true;
        }
        return false;
    }, [selection.selection, displayRows]);

    /** Bulk Delete is disabled only when an explicit selection includes
     *  a read-only row. 'all'-mode is now allowed: the server restricts
     *  all-mode bulk ops to headers this account ORIGINATES (ADR-0036),
     *  so it can never touch a header owned by another account. The
     *  typed-confirm dialog (count > threshold → type "delete N") still
     *  guards large all-mode deletes. */
    const bulkDeleteDisabled = selectionHasReadOnly;
    const bulkDeleteDisabledTitle = selectionHasReadOnly
        ? 'Selection includes rows whose canonical owner is elsewhere (investment header, or split counter-side). Delete those from the source register.'
        : 'Delete the active selection';

    /** Stable id for any display row — used to address the
     *  focused row across re-renders. Txn rows use the leg id,
     *  split-parents use the group id, split-legs use their own
     *  leg id. */
    function displayRowId(row: DisplayRow<BankRow>): string {
        if (row.kind === 'txn') return row.txn.id;
        if (row.kind === 'split-parent') return `g:${row.groupId}`;
        return row.leg.id;
    }


    function startEdit(headerId: string) {
        setEditingHeaderId(headerId);
        setIsCreatingNew(false);
        setSaveError(null);
    }

    function startCreate() {
        setIsCreatingNew(true);
        setEditingHeaderId(null);
        setSaveError(null);
    }

    function cancelEdit() {
        setEditingHeaderId(null);
        setIsCreatingNew(false);
        setDuplicateSource(null);
        setSaveError(null);
    }

    async function saveEdit(headerId: string, body: PatchTransactionRequest) {
        setSaveError(null);
        try {
            const savedEntry = await onPatch(headerId, body);
            setEditingHeaderId(null);
            // Move the keyboard cursor onto the saved row's NEW
            // id. Important when the patch reshaped the entry —
            // single → split changes the row id from the source
            // leg id to `g:<headerId>`, and the previous
            // focusedRowId would silently become a no-op.
            // patchMutation.onSuccess already swapped the entry
            // into the window via mutateEntries (same logical
            // index, no scroll change); we just need to retarget
            // focus.
            if (savedEntry !== null) {
                const newRowId =
                    savedEntry.kind === 'txn'
                        ? savedEntry.txn.id
                        : `g:${savedEntry.groupId}`;
                setFocusedRowId(newRowId);
            }
        } catch (error) {
            setSaveError(patchErrorMessage(error));
        }
    }

    async function saveCreate(body: CreateTransactionRequest) {
        setSaveError(null);
        try {
            await onCreate(body);
            setIsCreatingNew(false);
        } catch (error) {
            setSaveError(patchErrorMessage(error));
        }
    }

    // Seed focus from `focusIndex` exactly once per refresh
    // "season" AND scroll the focused row into view.
    // `initialTopMostItemIndex` on virtuoso is read once at mount
    // — before the hook's fetch has resolved focusIndex from -1
    // to its real value — so we can't rely on it for the
    // Show-Other-Side arrival. Instead, after the fetch lands
    // (focusIndex flips from -1 to a real index), we mirror the
    // row into focus state and call `scrollIntoView` explicitly.
    // virtuoso no-ops the scroll when the row is already in the
    // viewport.
    //
    // Gate: a ref holds the last focusIndex we seeded; we skip
    // if it matches the current focusIndex so steady-state
    // displayRows changes (group expand / window slide) don't
    // re-seed. But we RESET the ref to -1 whenever focusIndex
    // goes negative — the hook flips focusIndex to -1 on every
    // refresh before re-fetching, so the next positive value
    // counts as a fresh seed even when it happens to equal the
    // prior one. That's what lets post-PATCH (anchored refresh)
    // re-seed focus on the same row the user just saved.
    const seededFocusForIndexRef = useRef<number>(-1);
    useEffect(() => {
        if (focusIndex < 0) {
            seededFocusForIndexRef.current = -1;
            return;
        }
        if (seededFocusForIndexRef.current === focusIndex) return;
        // focusIndex is LOGICAL (post-eviction-stable); subtract
        // firstItemIndex to get the position inside the current
        // displayRows window.
        const targetRow = displayRows[focusIndex - firstItemIndex];
        if (targetRow === undefined) return;
        const rowId = displayRowId(targetRow);
        setFocusedRowId(rowId);
        // Focus seed sets the keyboard cursor + scrolls into view,
        // but does NOT pre-check the row. Focus and selection are
        // distinct (ADR-0023 §B); auto-checking the focused row on
        // Show-Other-Side / post-create surprised the user — they
        // expect the checkbox to reflect ONLY their explicit clicks.
        seededFocusForIndexRef.current = focusIndex;
        const handle = window.setTimeout(() => {
            // No `align` → virtuoso uses its default "smart" mode:
            // no-op when the row is fully visible, otherwise the
            // minimum scroll to bring it into view. The previous
            // `align: 'start'` yanked the saved row to the top of
            // the viewport after every save-refresh — disruptive
            // when the user was looking at the row mid-list. Smart
            // default keeps the row where it is when already
            // visible (the common case after save).
            virtuosoRef.current?.scrollIntoView({
                index: focusIndex,
            });
        }, 100);
        return () => window.clearTimeout(handle);
    }, [focusIndex, firstItemIndex, displayRows, setFocusedRowId]);

    // Column template: select / status / date+taxdate / check# /
    // payee+memo / category+tags / amount / balance. Category and
    // tags share one column (header reads "Category · tags") so
    // tags chips wrap beneath the category chip — mirrors the
    // payee+memo stacked pattern. Single signed Amount column
    // replaces the legacy Outflow/Inflow split.
    // Fits 1024px+ main panes; PR 5.3 will responsively collapse
    // less-essential columns on narrower viewports.
    //
    // Bank uses 8 cols (status/checkbox/date/check#/payee/cat/amt/bal).
    const COLS = BANK_COLS;

    // Header-checkbox state — three-state per ADR-0024. Each visible
    // header carries its createdAt so the selection predicate stays
    // honest in 'all' mode (rows newer than selectedAt are not part
    // of the selection — see ADR-0024 #3). A multi-split contributes
    // ONE header to the dedup set, not one per leg.
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
                // split-leg rows share their group's header id —
                // already collected via the parent.
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
        if (selectAllRef.current)
            selectAllRef.current.indeterminate = someVisibleSelected;
    }, [someVisibleSelected]);

    function handleSelectAll() {
        selection.toggleAll();
    }

    // Edge sentinels — honest cues for "yes, this really is the
    // end" so the user isn't second-guessing whether the scrollbar
    // means "absolute end" or "edge of the loaded window." Only
    // rendered when the hook says we're at the actual timeline
    // head / tail. Memoised so virtuoso doesn't re-mount the
    // Header / Footer on every parent render.
    const oldestPostedAtLabel = oldestEntry
        ? (() => {
              const r = oldestEntry.kind === 'txn'
                  ? oldestEntry.txn
                  : oldestEntry.legs[0]!;
              return formatLedgerDate(r.postedAt);
          })()
        : null;

    // Per-row renderer for virtuoso's `itemContent`. Branches on
    // the DisplayRow discriminator: split-parent / split-leg /
    // txn-in-edit-mode / regular-txn. virtuoso wraps the return
    // value in its own positioned container, so the rows don't
    // carry `position: absolute` / `transform: translateY(...)`
    // styles anymore — measurement happens via virtuoso's
    // ResizeObserver automatically.
    function renderRow(index: number, row: DisplayRow<BankRow>): JSX.Element {
        if (row.kind === 'split-parent') {
            const focusId = `g:${row.groupId}`;
            const canonical = row.legs[0]!;
            if (editingHeaderId === canonical.headerId) {
                // Editing a split → load every source-side leg as a
                // posting seed so the user sees the full breakdown
                // in the editor. The group's expansion state was
                // already collapsed by startEdit so the now-hidden
                // leg rows don't double-render below the editor.
                return (
                    <TxnRowEdit
                        ledgerId={ledgerId}
                        mode={{
                            kind: 'edit',
                            headerId: canonical.headerId,
                            sourceAccountId: canonical.accountId,
                            postings: row.legs.map((leg) => ({
                                legId: leg.id,
                                counterpartyAccountId: leg.counterpartyAccountId,
                                counterpartyAccountName: leg.counterpartyAccountName,
                                amount: leg.amount,
                                legMemo: leg.legMemo,
                            })),
                            payee: canonical.payee,
                            // Raw header memo (ADR-0025) — see the
                            // single-row branch above.
                            memo: canonical.headerMemo,
                            checkNumber: canonical.checkNumber,
                            postedAt: canonical.postedAt,
                            balanceAfter: canonical.balanceAfter,
                            // Slice 2c.6b: header-level tags. Per
                            // ADR-0009 tags live on the header so any
                            // leg's `tags` array is the same set.
                            tags: canonical.tags,
                            // Slice 2c.6 follow-up: capture open-time
                            // review state. Flips the save into an
                            // Accept flow — `approve: true` rides on
                            // the PATCH, button label becomes
                            // "Accept" (or "Merge & Accept").
                            needsReview: canonical.needsReview,
                        }}
                        payees={payees}
                        accounts={accounts}
                        accountPaths={accountPaths}
                        currency={currency}
                        cols={COLS}
                        onSavePatch={(body) =>
                            void saveEdit(canonical.headerId, body)
                        }
                        onCancel={cancelEdit}
                        isSaving={isPatching}
                        saveError={saveError}
                    />
                );
            }
            // Synthesize the representative parent row: the canonical leg
            // with the group's net amount + balance-after-last-leg, so the
            // strategy reads off `row` directly (no in-strategy group math).
            const parentRow: BankRow = {
                ...canonical,
                amount: groupAmount(row.legs),
                balanceAfter: groupBalanceAfter(row.legs),
            };
            const parentStatus = resolveRowStatus(canonical, today);
            return (
                <RegisterRow
                    strategy={bankRowStrategy}
                    variant="split-parent"
                    row={parentRow}
                    rowIndex={index}
                    accountPaths={accountPaths}
                    currency={currency}
                    today={today}
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
                    status={parentStatus}
                    cycleStatus={canonical.status}
                    statusHeaderId={canonical.headerId}
                    statusStatic={parentStatus === 'scheduled' || canonical.isPending}
                    expand={{
                        expanded: row.expanded,
                        onToggle: () => onToggleGroupExpanded(row.groupId),
                        count: row.legs.length,
                        groupId: row.groupId,
                    }}
                    focused={focusedRowId === focusId}
                    onFocus={() => setFocusedRowId(focusId)}
                    onCycleReconStatus={onCycleReconStatus}
                    onDoubleClickEdit={() => startEdit(canonical.headerId)}
                    onContextMenu={(anchor) =>
                        setContextMenu({
                            anchor,
                            target: canonical,
                            originatingSplit: true,
                            splitLegs: row.legs,
                        })
                    }
                    title="Click to focus · double-click to edit · right-click for actions"
                />
            );
        }
        if (row.kind === 'split-leg') {
            // Hide leg rows of the group currently in edit mode —
            // the editor (rendered on the parent above) replaces
            // the whole group's visual footprint.
            if (row.leg.headerId === editingHeaderId) {
                return <div data-edit-hidden="true" />;
            }
            return (
                <RegisterRow
                    strategy={bankRowStrategy}
                    variant="split-leg"
                    row={row.leg}
                    rowIndex={index}
                    accountPaths={accountPaths}
                    currency={currency}
                    today={today}
                    // Leg rows are non-interactive except focus-on-click;
                    // selection / status / edit / context-menu belong to
                    // the group, not the leg.
                    selected={false}
                    onToggleSelected={() => {}}
                    status={row.leg.status}
                    cycleStatus={row.leg.status}
                    onCycleReconStatus={() => {}}
                    focused={focusedRowId === row.leg.id}
                    onFocus={() => setFocusedRowId(row.leg.id)}
                />
            );
        }
        const txn = row.txn;
        if (editingHeaderId === txn.headerId) {
            return (
                <TxnRowEdit
                    ledgerId={ledgerId}
                    mode={{
                        kind: 'edit',
                        headerId: txn.headerId,
                        sourceAccountId: txn.accountId,
                        // Single-row → 1 posting seed.
                        postings: [
                            {
                                legId: txn.id,
                                counterpartyAccountId: txn.counterpartyAccountId,
                                counterpartyAccountName: txn.counterpartyAccountName,
                                amount: txn.amount,
                                legMemo: txn.legMemo,
                            },
                        ],
                        payee: txn.payee,
                        // Raw header memo only (ADR-0025) — falling
                        // back to txn.memo would load a leg-memo into
                        // the umbrella field, then save would promote
                        // it to the header memo column.
                        memo: txn.headerMemo,
                        checkNumber: txn.checkNumber,
                        postedAt: txn.postedAt,
                        balanceAfter: txn.balanceAfter,
                        // Slice 2c.6b: header-level tags.
                        tags: txn.tags,
                        // Slice 2c.6 follow-up: open-time review
                        // state drives the Accept flow.
                        needsReview: txn.needsReview,
                    }}
                    payees={payees}
                    accounts={accounts}
                    accountPaths={accountPaths}
                    currency={currency}
                    cols={COLS}
                    onSavePatch={(body) =>
                        void saveEdit(txn.headerId, body)
                    }
                    onCancel={cancelEdit}
                    isSaving={isPatching}
                    saveError={saveError}
                />
            );
        }
        // Read-only row guards (mirror the former BankTxnRow): an
        // investment-owned cash leg (canonical owner is the brokerage
        // register) or a split counter-side (source side owns edit /
        // delete). Recon-status cycling stays enabled either way.
        const isInvestmentOwned = isInvestmentOwnedRow(txn);
        const isSplitCounter = !isInvestmentOwned && txn.txnGroupId !== null;
        const isReadOnly = isInvestmentOwned || isSplitCounter;
        const txnStatus = resolveRowStatus(txn, today);
        const txnScheduled = txnStatus === 'scheduled';
        return (
            <RegisterRow
                strategy={bankRowStrategy}
                variant="txn"
                row={txn}
                rowIndex={index}
                accountPaths={accountPaths}
                currency={currency}
                today={today}
                selected={selection.isSelected(txn.headerId, txn.createdAt)}
                focused={focusedRowId === txn.id}
                onToggleSelected={(shiftKey) =>
                    shiftKey
                        ? selection.extendSelectionTo(txn.headerId, orderedHeaderIds)
                        : selection.toggleId(txn.headerId)}
                selectLabel={`Select transaction ${txn.id}`}
                onFocus={() => setFocusedRowId(txn.id)}
                cmdClickToggles
                readOnly={isReadOnly}
                status={txnStatus}
                cycleStatus={txn.status}
                statusHeaderId={txn.headerId}
                statusStatic={txnScheduled || txn.isPending}
                needsReview={txn.needsReview}
                onDoubleClickEdit={() => startEdit(txn.headerId)}
                onContextMenu={(anchor) =>
                    setContextMenu({ anchor, target: txn })
                }
                onCycleReconStatus={onCycleReconStatus}
                title={
                    txn.needsReview
                        ? 'Needs review · right-click → Accept to clear the flag'
                        : 'Click to focus · double-click to edit · right-click for actions'
                }
            />
        );
    }

    return (
        <TagColorsProvider ledgerId={ledgerId}>
        <RegisterShell
            columns={COLS}
            initialLoaded={initialLoaded}
            initialError={initialError}
            isEmpty={isEmpty}
            filterActive={filterActive}
            toolbar={(
                    // Combined controls bar (fold #4): status-filter tabs
                    // (left) + New-transaction button & keyboard hint
                    // (right), one row. Identical to investment.
                    <RegisterControlsBar
                        statusFilter={statusFilter}
                        onStatusFilterChange={onStatusFilterChange}
                        sort={sort}
                        onSortChange={onSortChange}
                        isInvestment={isInvestment}
                        filter={filter}
                        onFilterChange={onFilterChange}
                        categories={accounts.filter((a) => a.accountType === 'category')}
                        tags={tagsQuery.data ?? []}
                        resultCount={filterResultCount}
                        statusCounts={statusCounts}
                        onNew={startCreate}
                        newDisabled={isCreatingNew}
                    />
                )}
                newTxnEditor={isCreatingNew ? (
                        <TxnRowEdit
                            ledgerId={ledgerId}
                            mode={{
                                kind: 'new',
                                sourceAccountId: currentAccountId,
                                prefill: duplicateSource ?? undefined,
                            }}
                            payees={payees}
                            accounts={accounts}
                            accountPaths={accountPaths}
                            currency={currency}
                            cols={COLS}
                            onSaveCreate={(body) => void saveCreate(body)}
                            onCancel={cancelEdit}
                            isSaving={isCreating}
                            saveError={saveError}
                        />
                    ) : null}
                headerCells={(
                    <>
                    <RegisterLeadHeaderCells
                        selectAllRef={selectAllRef}
                        allVisibleSelected={allVisibleSelected}
                        onToggleAll={handleSelectAll}
                        disabled={visibleRowsForSelection.length === 0}
                        selectAllLabel="Select all transactions in this account matching the current filter"
                    />
                    <span role="columnheader" className="truncate">
                        Date
                    </span>
                    <span role="columnheader" className="truncate">
                        Check #
                    </span>
                    <span role="columnheader" className="truncate">
                        Payee · memo
                    </span>
                    <span role="columnheader" className="truncate">
                        Category · tags
                    </span>
                    <span role="columnheader" className="truncate text-right">Amount</span>
                    <span role="columnheader" className="truncate text-right">Balance</span>
                    </>
                )}
            >
            <RegisterScrollSurface
                scrollRef={setScrollParent}
                scrollRegionId="register-scroll-region"
                ariaRowCount={rowCount}
                scrollTrack={sort.column === 'date' ? (
                    <RegisterScrollTrack
                        // Date-asc → oldest-first buckets so the rail's top
                        // matches the list's top (newest-first is the default).
                        buckets={sort.dir === 'asc' ? [...indexBuckets].reverse() : [...indexBuckets]}
                        currentYearMonth={viewportYearMonth ?? currentYearMonth}
                        onSeek={onSeekBucket}
                    />
                ) : null}
            >
                {/* virtuoso owns row positioning + measurement;
                    customScrollParent wires it to the surface
                    above so scroll events drive its windowing. */}
                <RegisterVirtualList
                    virtuosoRef={virtuosoRef}
                    scrollParent={scrollParent}
                    rows={displayRows}
                    getRowId={displayRowId}
                    renderRow={renderRow}
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
                    onLoadNewer={onLoadNewer}
                    onLoadOlder={onLoadOlder}
                    firstItemIndex={firstItemIndex}
                    initialTopMostItemIndex={focusIndex >= 0 ? focusIndex : 0}
                    atTimelineHead={atTimelineHead}
                    atTimelineTail={atTimelineTail}
                    oldestLabel={oldestPostedAtLabel}
                />
            </RegisterScrollSurface>
            <RegisterDateJumpPopover
                buckets={[...indexBuckets]}
                onSeek={onSeekBucket}
            />

            <RegisterBulkActionBar
                selectedCount={selectedCount}
                selectedSum={selectedSum}
                currency={currency}
                loading={loadingOlder || loadingNewer}
                bulkDeleteDisabled={bulkDeleteDisabled}
                bulkDeleteDisabledTitle={bulkDeleteDisabledTitle}
                onBulkSetReconStatus={onBulkSetReconStatus}
                onRequestBulkDelete={() => setPendingDelete({ kind: 'bulk' })}
                onClearSelection={() => selection.clear()}
                statusFilter={statusFilter}
                onBulkUnhide={onBulkUnhide}
                bulkUnhidePending={bulkUnhidePending}
                onOpenMoveDialog={onOpenMoveDialog}
                moveDisabled={selectionHasReadOnly}
                moveDisabledTitle="Selection includes rows whose canonical owner is elsewhere (investment header, or split counter-side). Move those from the source register."
            />
            {/* Row context menu. Rendered as a sibling at the section
                root so it isn't clipped by the virtualizer's
                position:absolute children. The component handles its
                own outside-click + Esc dismissal; we just provide
                state and items. */}
            {contextMenu ? (
                <ContextMenu
                    anchor={contextMenu.anchor}
                    items={buildBankRowMenuItems(contextMenu.target, {
                        onApprove,
                        onDuplicate: (target) => {
                            // Open the new-transaction form prefilled
                            // from the source. One path: a split-parent
                            // carries all its legs (every posting), a
                            // single row carries just itself — both via
                            // rowsToDuplicatePrefill. posted_at defaults
                            // to today in the form's own derivation.
                            const rows = contextMenu.splitLegs ?? [target];
                            setDuplicateSource(rowsToDuplicatePrefill(rows));
                            startCreate();
                        },
                        onCreateReminder: (target) => {
                            // ADR-0051 slice C: reuse the Duplicate row->prefill
                            // mapping, but route it to the reminder editor (with
                            // a blank schedule) instead of the new-txn form.
                            const rows = contextMenu.splitLegs ?? [target];
                            setReminderFrom({
                                sourceAccountId: currentAccountId,
                                prefill: rowsToDuplicatePrefill(rows),
                            });
                        },
                        onShowOtherSide,
                        onRequestDelete: (target) =>
                            setPendingDelete({ kind: 'single', target }),
                    }, { originatingSplit: contextMenu.originatingSplit })}
                    onClose={closeContextMenu}
                />
            ) : null}
            {/* Destructive-action confirm. Drives single-row + bulk
                delete; the dialog stays mounted under the modal scrim
                until the user confirms or cancels. Copy varies by
                policy: manual entries hard-delete (cannot be undone),
                feed/import rows soft-hide (reversible via "show
                hidden" once that lands). */}
            <RegisterDeleteConfirm
                pending={pendingDelete}
                selectedCount={selectedCount}
                allMode={selection.selection.kind === 'all'}
                isConfirming={pendingDelete?.kind === 'bulk' && isBulkDeleting}
                onConfirmSingle={(target) => void onDelete(target)}
                onConfirmBulk={onBulkDelete}
                onCancel={() => setPendingDelete(null)}
            />
            {reminderFrom !== null ? (
                <ReminderEditorDialog
                    ledgerId={ledgerId}
                    reminderId={null}
                    fromTransaction={reminderFrom}
                    onClose={() => setReminderFrom(null)}
                    onSaved={() => setReminderFrom(null)}
                />
            ) : null}
        </RegisterShell>
        </TagColorsProvider>
    );

}

/**
 * A register entry whose rows are all `BankRow` — the homogeneous
 * shape of a bank-domain account's window (ADR-0030 §2). Mirrors the
 * `RegisterEntry` union with the row type pinned to `BankRow`.
 */
type BankEntry =
    | { kind: 'txn'; txn: BankRow; groupId: null; legs: null }
    | { kind: 'group'; txn: null; groupId: string; legs: BankRow[] };

/**
 * Narrow the account-scoped window to `BankEntry[]` on the `kind`
 * discriminant. The bank register only ever loads a bank-domain
 * account (RegisterRouter dispatches investment accounts elsewhere),
 * so every row is `kind: 'bank'`; a non-bank row would be a routing
 * defect and is dropped rather than coerced into the wrong shape.
 */
function narrowToBankEntries(
    entries: readonly RegisterEntry[],
): BankEntry[] {
    const out: BankEntry[] = [];
    for (const entry of entries) {
        if (entry.kind === 'txn') {
            if (entry.txn.kind !== 'bank') continue;
            out.push({
                kind: 'txn',
                txn: entry.txn,
                groupId: null,
                legs: null,
            });
            continue;
        }
        const legs = entry.legs.filter(
            (l): l is BankRow => l.kind === 'bank',
        );
        if (legs.length === 0) continue;
        out.push({
            kind: 'group',
            txn: null,
            groupId: entry.groupId,
            legs,
        });
    }
    return out;
}

function findById<T extends { id: string }>(
    items: readonly T[] | undefined,
    id: string,
): T | undefined {
    if (!items) return undefined;
    return items.find((item) => item.id === id);
}

