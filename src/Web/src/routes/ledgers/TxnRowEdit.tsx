import {
    useEffect,
    useId,
    useMemo,
    useRef,
    useState,
    type KeyboardEvent,
    type ReactNode,
} from 'react';
import { useQuery } from '@tanstack/react-query';

import {
    ApiError,
    fetchFrequentCounterparties,
    fetchMergeCandidates,
    fetchSimilarPayees,
    fetchTags,
} from '@/lib/api';
import { DateField } from './components/DateField';
import { formatCurrency } from '@/lib/money';
import type {
    AccountSummary,
    CreateTransactionRequest,
    FrequentCounterpartiesResponse,
    PatchTransactionRequest,
    PayeeSuggestion,
    TransactionPosting,
} from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Typeahead } from '@/components/ui/Typeahead';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { pickableCounterparties } from '@/lib/accountPath';
import { postingIssues, validatePostings } from './bank-edit/validation';
import { useTxnRowDraft } from './bank-edit/hooks/useTxnRowDraft';
import { TagsInput } from './bank-edit/fields/TagsInput';
import { SimilarPayeesPanel } from './bank-edit/fields/SimilarPayeesPanel';
import { MergeCandidatesPanel } from './bank-edit/fields/MergeCandidatesPanel';
import { SplitsGrid } from './bank-edit/fields/SplitsGrid';

// Transaction editor (ADR-0025). One component, one state shape,
// one save handler for every transaction mutation: new single,
// new split, edit single, edit split, convert single→split,
// convert split→single, reorder, amount/category/memo edits.
//
// The editor's mental model = a transaction is a list of postings.
// Each posting has (counterparty, amount, legMemo). Single-row =
// length 1; split = length > 1. Switching between them is just
// adding or removing postings — no special "convert" affordance.
//
// Affordances per posting. The splits grid owns all of these now —
// see bank-edit/fields/SplitsGrid.tsx and bank-edit/README.md:
//   - a visible row number, so a validation message can name one
//   - category picker (AccountCategoryPicker, ADR-0043) beside a
//     reserved per-posting tags slot
//   - leg-memo textarea, fixed height
//   - amount input
//   - a drag handle, plus a row menu carrying Move up / Move down
//     (Alt+↑ / Alt+↓) and Remove split
//
// Add-posting affordance: an "Add split" button in the legs region's
// sticky footer. That REPLACED a ghost row at the bottom of the list,
// and the reversal is deliberate: a ghost is the list's last element,
// so once the list scrolls inside a fixed-height viewport the only way
// to add a posting scrolls away with it. ADR-0025's 2026-09-16
// amendment records it against the original ADR-0023 §C pattern.

/** A single posting to seed a NEW transaction with (Duplicate path).
 *  No `legId` — every duplicated posting is a fresh leg. */
export interface PostingPrefill {
    counterpartyAccountId: string | null;
    amount: number;
    legMemo?: string | null;
}

/** Optional pre-fill for the new-transaction form (Duplicate path).
 *  Header fields + the postings to clone. A single-row duplicate seeds
 *  ONE posting; a split duplicate seeds N — the SAME path: the editor
 *  maps each prefill posting to a fresh draft. A single row is just the
 *  N=1 case, so there is no special-casing of splits. posted_at
 *  intentionally defaults to today. */
export interface TxnRowNewPrefill {
    payee?: string | null;
    memo?: string | null;
    checkNumber?: string | null;
    postings?: readonly PostingPrefill[];
}

/** A posting seed for edit mode. One per existing leg on the
 *  source account; the editor materialises a draft per seed. */
export interface PostingSeed {
    legId: string;
    counterpartyAccountId: string | null;
    counterpartyAccountName: string | null;
    amount: number;
    legMemo: string | null;
}

export type TxnRowMode =
    | {
          kind: 'new';
          sourceAccountId: string;
          prefill?: TxnRowNewPrefill;
          /** Optional initial posted date ('YYYY-MM-DD'); defaults to today.
           *  The reminders occurrence dialog passes the occurrence date so the
           *  editor opens on it instead of today (ADR-0049). */
          postedAt?: string;
      }
    | {
          kind: 'edit';
          headerId: string;
          sourceAccountId: string;
          /** All source-side legs of the header being edited.
           *  For a single-row transaction this is a single-element
           *  array; for an N-split it's N elements. */
          postings: readonly PostingSeed[];
          /** The header-level payee + memo + posted_at to seed
           *  the form's header fields. Pulled from any one of
           *  the header's legs at the caller. */
          payee: string | null;
          memo: string | null;
          checkNumber: string | null;
          postedAt: string;
          /** Tax / transaction date to seed the editor's Tax date field, or null
           *  when the row has none.
           *
           *  REQUIRED, not optional, on purpose: the editor sends transactedAt on
           *  every save, so a caller that forgot to seed it would silently CLEAR a
           *  tax date the transaction already had. Optional would have made that a
           *  runtime data-loss bug; required makes it a compile error. */
          transactedAt: string | null;
          balanceAfter: number | null;
          /** Slice 2c.6b: header-level tags currently applied to
           *  this transaction. The editor's TagsInput seeds from
           *  this list; the save handler sends the final set
           *  (replace semantics). */
          tags: readonly string[];
          /** Slice 2c.6 follow-up: whether the row had
           *  `needs_review = true` when the editor opened. Flips
           *  the save into an "Accept" flow:
           *    - button label becomes "Accept" (or "Merge & Accept"
           *      when mergeFromHeaderId is armed),
           *    - the PATCH body carries `approve: true` so
           *      `needs_review` clears in the same transaction.
           *  Captured at open time (not from a live query against
           *  the row) so saving an already-accepted row that was
           *  opened while flagged still re-clears the flag — and
           *  saving a re-opened approved row doesn't accidentally
           *  re-trigger Accept semantics. */
          needsReview: boolean;
      };

export interface TxnRowEditProps {
    /** Slice 2c.6c: the editor fetches similar-payee suggestions
     *  via this ledger scope when opened in edit mode on a
     *  single-posting row. Required because TxnRowEdit owns the
     *  query (RegisterPage shouldn't have to fetch + thread the
     *  result per-row). */
    ledgerId: string;
    mode: TxnRowMode;
    /** Pre-fetched payee suggestions for the payee Typeahead. */
    payees: readonly PayeeSuggestion[];
    /** Every account in the ledger. Drives the counterparty
     *  Typeahead on each posting row. */
    accounts: readonly AccountSummary[];
    /** Pre-computed slash-joined paths keyed by account id. */
    accountPaths: Map<string, string>;
    /** Currency for the running-total readout. */
    currency: string;
    /** Register grid template (8 columns, same as
     *  RegisterPage's `COLS`). The editor uses the same template
     *  so root + leg fields align vertically with the register's
     *  static rows and the column-header strip above. */
    cols: string;
    /** Fire a patch (edit mode). */
    onSavePatch?: (body: PatchTransactionRequest) => void;
    /** Fire a create (new mode). */
    onSaveCreate?: (body: CreateTransactionRequest) => void;
    onCancel: () => void;
    isSaving: boolean;
    saveError: string | null;
    /** When false, the editor does NOT cancel on outside click — for hosts that
     *  manage their own dismissal (the reminders occurrence dialog has its own
     *  backdrop / Esc / ×, ADR-0049). Defaults to true (the register behavior). */
    cancelOnOutsideClick?: boolean;
    /** Optional left-aligned footer content (e.g. the reminders dialog's Skip
     *  action), rendered alongside the Split link + Cancel / Save (ADR-0049). */
    footerLeading?: ReactNode;
    /** Override the primary button's label + busy label. The reminders
     *  occurrence dialog posts the occurrence, so it passes "Post" / "Posting…";
     *  defaults to "Save" / "Saving…" (the merge / needs-review accept labels
     *  still take precedence when those flows are active). */
    submitLabel?: string;
    submittingLabel?: string;
}

// Date-input helpers (toDateInputValue / todayInputValue /
// shiftDateInputValue) moved to `@/lib/dates` so the date-handling
// rules are centralized — same module that owns `formatLedgerDate`
// and the UTC-vs-local categorization.

/** Surface an ApiError's `.detail` or fall back to a generic
 *  phrase. Used by the parent's mutation error handler too. */
export function patchErrorMessage(error: unknown): string {
    if (error instanceof ApiError) return error.detail;
    if (error instanceof Error && error.message.length > 0) return error.message;
    return 'Save failed.';
}

// --------------------------------------------------------------------
// Editor
// --------------------------------------------------------------------

export function TxnRowEdit({
    ledgerId,
    mode,
    payees,
    accounts,
    accountPaths,
    currency,
    cols,
    onSavePatch,
    onSaveCreate,
    onCancel,
    isSaving,
    saveError,
    cancelOnOutsideClick = true,
    footerLeading,
    submitLabel,
    submittingLabel,
}: TxnRowEditProps) {
    const dateId = useId();
    const containerRef = useRef<HTMLDivElement | null>(null);
    const headerMemoRef = useRef<HTMLTextAreaElement | null>(null);

    /** Auto-grow the memo textarea to fit its content, capped at
     *  ~4 lines (96px) — data scan showed p99 memo length is
     *  ~120 chars (~1 line); the ~1% that exceeds 4 lines scrolls
     *  inside the textarea rather than expanding the form. */
    function adjustMemoHeight(el: HTMLTextAreaElement | null) {
        if (el === null) return;
        el.style.height = 'auto';
        const maxPx = 96;
        el.style.height = `${Math.min(el.scrollHeight, maxPx)}px`;
    }
    useEffect(() => {
        adjustMemoHeight(headerMemoRef.current);
    }, []);

    // All editable state — the seven header/posting fields, the posting
    // mutators and the auto-focus marker. Destructured into the same local
    // names the JSX already used, so moving it out of the shell changed no
    // call site. See hooks/useTxnRowDraft.ts for the three properties
    // (once-only seeding, synchronous addPosting, stable mutator identity)
    // that the consolidation had to preserve.
    const {
        draft: { payee, headerMemo, checkNumber, postedAt, transactedAt, tags, postings },
        setPayee,
        setHeaderMemo,
        setCheckNumber,
        setPostedAt,
        setTransactedAt,
        setTags,
        patchPosting,
        addPosting,
        removePosting,
        reorderPostings,
        movePosting,
        focusKey,
        setFocusKey,
    } = useTxnRowDraft(mode);

    // Slice 2c.6c: similar-payee recall. Static fetch at row-open
    // (per design — no typeahead refetch). The server returns
    // empty for non-bank-feed rows / no matches / non-single-
    // posting prior rows, so the chip row simply doesn't render
    // when the result is empty. Enabled only for edit-mode
    // single-posting opens; splits don't fit the one-chip = one-
    // (payee, category) shape Tier 1 returns.
    const editSingleHeaderId =
        mode.kind === 'edit' && mode.postings.length === 1
            ? mode.headerId
            : null;
    const similarPayees = useQuery({
        queryKey: ['similar-payees', ledgerId, editSingleHeaderId],
        queryFn: () => fetchSimilarPayees(ledgerId, editSingleHeaderId!),
        enabled: editSingleHeaderId !== null,
        staleTime: Infinity, // static list, no refetch while editor is open
    });

    // Slice 2c.6d: merge candidates. Static fetch at row-open
    // (same UX as similar-payees — no typeahead). Surfaces
    // manual rows whose source-account aggregate matches the
    // target, within ±7 days. Clicking a chip pre-fills the
    // editor with the candidate's payee/memo/tags/postings AND
    // arms `mergeFromHeaderId` so the next save stamps the
    // manual row as merged into this one.
    //
    // Enabled for ANY edit-mode row (not just needs_review) —
    // the matching algorithm decides whether anything fits, and
    // empty results hide the panel. We use mode.headerId here
    // (not editSingleHeaderId) so split-target editors also see
    // the panel; the server still only returns rows whose
    // aggregated source amount matches.
    const editHeaderId = mode.kind === 'edit' ? mode.headerId : null;
    const mergeCandidates = useQuery({
        queryKey: ['merge-candidates', ledgerId, editHeaderId],
        queryFn: () => fetchMergeCandidates(ledgerId, editHeaderId!),
        enabled: editHeaderId !== null,
        staleTime: Infinity,
    });
    // Selected merge source — set when the user clicks a "Possible
    // match" chip, sent on the next save. Null = no merge stamp.
    // The chip click also pre-fills the editor's form state via
    // the apply helper below; clearing the selection via the same
    // helper is responsible for resetting the merge id too.
    const [mergeFromHeaderId, setMergeFromHeaderId] = useState<string | null>(null);

    const sourceAccountId =
        mode.kind === 'new' ? mode.sourceAccountId : mode.sourceAccountId;

    // ADR-0043: counterparty selection is id-based via the shared
    // AccountCategoryPicker. Eligibility = the pickable set (active,
    // non-system) minus the source account; the picker displays an
    // existing counterparty's name from the FULL accounts map, so a
    // system-account counterparty (the bank-feed sync stamps every
    // incoming row with Uncategorized) still round-trips even though
    // it isn't offered for a fresh pick. That replaces the old
    // text → resolveCounterpartyId round-trip + its system-account
    // lookup workaround.
    const pickableCounterpartyIds = useMemo(() => {
        const ids = new Set<string>();
        for (const a of pickableCounterparties(accounts, accountPaths)) {
            if (a.id !== sourceAccountId) ids.add(a.id);
        }
        return ids;
    }, [accounts, accountPaths, sourceAccountId]);

    const isEligibleCounterparty = useMemo(
        () => (a: AccountSummary) => pickableCounterpartyIds.has(a.id),
        [pickableCounterpartyIds],
    );

    // The source account's most-used counterparties, pinned to the
    // top of each posting's picker (matches the investment editor).
    const frequentQuery = useQuery({
        queryKey: ['frequent-counterparties', ledgerId, sourceAccountId],
        queryFn: () => fetchFrequentCounterparties(ledgerId, sourceAccountId),
        staleTime: 60_000,
    });
    const frequent: FrequentCounterpartiesResponse | null =
        frequentQuery.data ?? null;

    // Ledger tag dictionary — powers the TagsInput autocomplete (existing
    // names + colour swatch + usage) and create-on-first-use. Shared cache
    // key with the register filter + colour provider.
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
        staleTime: 60_000,
    });

    // Live running total (informational; sum-constraint is per-
    // posting at the server, not across the transaction).
    const sourceTotal = useMemo(() => {
        let n = 0;
        for (const p of postings) {
            const v = Number(p.amount);
            if (!Number.isNaN(v)) n += v;
        }
        return n;
    }, [postings]);

    // Click-outside cancels the edit (modern web pattern) — unless the host
    // manages its own dismissal (the reminders occurrence dialog, ADR-0049).
    useEffect(() => {
        if (!cancelOnOutsideClick) return;
        const onPointerDown = (event: PointerEvent) => {
            if (containerRef.current?.contains(event.target as Node)) return;
            onCancel();
        };
        document.addEventListener('pointerdown', onPointerDown, true);
        return () => {
            document.removeEventListener('pointerdown', onPointerDown, true);
        };
    }, [onCancel, cancelOnOutsideClick]);

    // Validation — Save is disabled until every posting parses to
    // a valid (parseable amount, recognised counterparty) shape.
    // Zero-amount postings ARE allowed: paycheck splits routinely
    // carry $0 line items (Medicare Surtax / 401(k) overflow /
    // bonus accrual) that flicker positive in some pay periods and
    // stay $0 in others. The DB places no constraint either; the
    // earlier "must be non-zero" client rule was an overreach that
    // locked users out of merging into any paycheck-style target.
    const validation = useMemo(() => validatePostings(postings), [postings]);
    // The same defects as data, for the splits grid: it marks the offending
    // ROW and its summary strip links to the offending FIELD, neither of which
    // a sentence can do. One rule set behind both — see bank-edit/validation.ts.
    const issues = useMemo(() => postingIssues(postings), [postings]);
    // When folding into a merge candidate, the editor's postings
    // / payee / memo are about to be discarded — they don't need
    // to validate. Save stays enabled purely on the merge stamp.
    const saveDisabled =
        isSaving
        || (mergeFromHeaderId === null && validation.length > 0);

    // The primary button's label tracks what the save actually
    // does. A needs_review row is being Accepted; when the user
    // armed a merge candidate, the action becomes a Fold-into:
    // the editor row vanishes, the candidate stays as the
    // canonical surviving row (inverted-merge direction). Form
    // edits in the editor are moot when folding — the label
    // makes the direction explicit so users don't expect their
    // edits to apply.
    const isNeedsReviewAccept = mode.kind === 'edit' && mode.needsReview;
    const isMerging = mergeFromHeaderId !== null;
    const saveButtonLabel = (() => {
        if (isSaving) {
            if (isMerging) return 'Folding…';
            if (isNeedsReviewAccept) return 'Accepting…';
            return submittingLabel ?? 'Saving…';
        }
        if (isMerging) return 'Fold into selected →';
        if (isNeedsReviewAccept) return 'Accept';
        return submitLabel ?? 'Save';
    })();

    function buildSaveBody():
        | { kind: 'create'; body: CreateTransactionRequest }
        | { kind: 'patch'; body: PatchTransactionRequest }
        | null
    {
        // Inverted-merge direction: when a candidate is selected, the editor
        // row is about to become a loser — its content is moot. Send a minimal
        // PATCH that just stamps the merge (+ implicit approve to keep state
        // coherent if the row is ever surfaced again). The candidate stays
        // untouched, so there's nothing to apply to the editor row's payee /
        // memo / postings / tags.
        //
        // FIRST, before the postings are read at all. This sat AFTER the
        // validation loop below, which returns null on a posting with no
        // counterparty — so folding a row that had none silently did nothing:
        // `saveDisabled` deliberately skips validation while merging, so the
        // button said "Fold into selected →" and was enabled, and the click
        // reached a builder that bailed before it ever saw the merge stamp.
        // An uncategorised imported row is exactly that shape, and
        // uncategorised imported rows are most of what needs review.
        // `mode.kind === 'edit'` is the type narrowing the create early-return
        // used to provide when this block sat further down; merging is an
        // edit-only action anyway (the candidates query is gated on an edit
        // header), so it costs nothing to state it.
        if (mergeFromHeaderId !== null && mode.kind === 'edit') {
            // Merge stamp ONLY — no `approve`. Clearing needs_review on the loser
            // is the server's job now (TransactionsRepository's merge branch), so
            // sending the flag would be a second mechanism for one invariant and
            // would imply a caller that omits it gets a different outcome. It does
            // not. Identical to the body the investment editor sends.
            return { kind: 'patch', body: { mergeFromHeaderId } };
        }

        const items: TransactionPosting[] = [];
        for (const p of postings) {
            const amount = Number(p.amount);
            if (p.counterpartyId === null || Number.isNaN(amount)) return null;
            items.push({
                legId: p.legId,
                counterpartyAccountId: p.counterpartyId,
                amount,
                legMemo: p.legMemo.trim().length === 0 ? null : p.legMemo.trim(),
            });
        }

        if (mode.kind === 'new') {
            const body: CreateTransactionRequest = {
                postedAt: `${postedAt}T00:00:00.000Z`,
                // Blank means "no distinct tax date", which mig 189 stores as the
                // posted date — NOT null. Both date columns are NOT NULL, and
                // since mig 230 the server rejects an explicit null on either
                // rather than ignoring it. Same value on both paths so create and
                // patch cannot drift.
                transactedAt: `${transactedAt.length === 0 ? postedAt : transactedAt}T00:00:00.000Z`,
                payee: payee.trim().length === 0 ? null : payee.trim(),
                memo: headerMemo.trim().length === 0 ? null : headerMemo.trim(),
                checkNumber:
                    checkNumber.trim().length === 0 ? null : checkNumber.trim(),
                sourceAccountId: mode.sourceAccountId,
                postings: items,
                tags,
            };
            return { kind: 'create', body };
        }

        const body: PatchTransactionRequest = {
            // Every header field, every save. Since mig 230 an explicit null
            // CLEARS the column, so an emptied payee / memo / check box now
            // actually empties it — under the old override layer the server read
            // the null as "leave alone" and handed the old text back.
            payee: payee.trim().length === 0 ? null : payee.trim(),
            memo: headerMemo.trim().length === 0 ? null : headerMemo.trim(),
            checkNumber:
                checkNumber.trim().length === 0 ? null : checkNumber.trim(),
            postedAt: `${postedAt}T00:00:00.000Z`,
            // See the create path: blank is the posted date, never null.
            transactedAt: `${transactedAt.length === 0 ? postedAt : transactedAt}T00:00:00.000Z`,
            postings: {
                sourceAccountId: mode.sourceAccountId,
                items,
            },
            tags,
            // Slice 2c.6 follow-up: saving a needs_review row IS
            // the Accept action — implicit approve flag clears the
            // flag in the same transaction. Non-flagged rows omit
            // the field so a no-op edit on an already-approved row
            // stays semantically idempotent.
            approve: mode.needsReview ? true : undefined,
        };
        return { kind: 'patch', body };
    }

    function handleSave() {
        if (saveDisabled) return;
        const built = buildSaveBody();
        if (built === null) return;
        if (built.kind === 'create') onSaveCreate?.(built.body);
        else onSavePatch?.(built.body);
    }

    function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
        // `defaultPrevented` is the whole guard, and it is load-bearing.
        // Escape inside an OPEN category picker used to cancel the entire
        // transaction — dismiss a dropdown you opened by mistake, lose
        // thirteen legs of a paycheck split. AccountCategoryPicker already
        // calls preventDefault() when it closes its own panel on Escape; this
        // handler simply never looked, so the event closed the panel and then
        // kept going.
        //
        // Deliberately NOT stopPropagation inside the picker instead. Typeahead
        // (the payee field) documents the opposite choice at its own keyboard
        // contract — "close the popover; let the event bubble so the parent
        // form's cancel handler can fire" — and marks it by NOT calling
        // preventDefault. Reading the flag honours both components' existing
        // signalling rather than overriding one of them.
        if (event.key === 'Escape' && !event.defaultPrevented) {
            event.preventDefault();
            onCancel();
        }
    }

    // Two render branches that share `postings: PostingDraft[]`
    // state under the hood (ADR-0025 unchanged). Single-row gets a
    // compact one-row form matching the register's columns; multi-
    // split gets the root + leg-list layout. Crossing between them
    // is just an `addPosting()` (single → split) or removing legs
    // until length === 1 (split → single). The shared outer
    // wrapper handles Escape / click-outside cancel + the
    // save-error footer.
    const onlyPosting = postings[0]!;

    // Declared once and rendered in BOTH layouts. It used to live only inside the
    // single-posting arm, so on a split row the candidates query fired, the server
    // answered, the data landed in cache — and nothing rendered it. Merge was
    // unreachable from any split bank row, which is exactly where an imported
    // duplicate of a paycheck would need folding in. The investment editor mounts
    // its panel once for the same reason: it has only one layout to fall out of.
    const mergePanel = (
        <MergeCandidatesPanel
            candidates={mergeCandidates.data ?? []}
            accountPaths={accountPaths}
            selectedHeaderId={mergeFromHeaderId}
            disabled={isSaving}
            onSelect={(c) => {
                // Inverted-merge direction: picking a candidate means "fold this
                // editor row INTO the candidate." The candidate is the surviving
                // canonical row — its data is preserved as-is. The editor's form
                // fields are moot once a candidate is selected (the row vanishes
                // on save). No pre-fill — that would silently overwrite the
                // candidate's content with a confused copy. Toggle behaviour
                // mirrors the old direction.
                setMergeFromHeaderId(
                    mergeFromHeaderId === c.headerId ? null : c.headerId);
            }}
        />
    );

    return (
        <div
            ref={containerRef}
            role="row"
            data-editing="true"
            data-creating={mode.kind === 'new' || undefined}
            style={{ width: '100%' }}
            className="border-y border-accent/40 bg-accent-soft/10"
            onKeyDown={handleKeyDown}
        >
        {postings.length === 1 ? (
            // ──────────────────────────────────────────────────
            // Single-row layout (~95% of edits). One register-
            // shaped grid row of inputs. The "Split this
            // transaction →" link below-left expands into the
            // multi-split branch by adding an empty posting.
            // ──────────────────────────────────────────────────
            <>
                <div
                    className="grid items-start gap-2 px-3 py-2"
                    style={{ gridTemplateColumns: cols }}
                >
                    <span />
                    <span />
                    {/* Date and Tax date stack in ONE grid cell, mirroring how Memo sits
                        under Payee — so the tax date lands on the second line, aligned with
                        the memo, and Check # keeps its own column. Two grid cells here made
                        the top row too tight and truncated the tax input. */}
                    <div className="flex min-w-0 flex-col gap-2">
                        <DateField
                            label="Date"
                            id={dateId}
                            value={postedAt}
                            onChange={setPostedAt}
                            disabled={isSaving}
                            autoFocus
                        />
                        <DateField
                            label="Tax date"
                            value={transactedAt}
                            onChange={setTransactedAt}
                            disabled={isSaving}
                            hint="Blank = same as posted"
                        />
                    </div>
                    <label className="flex min-w-0 flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Check #</span>
                        <input
                            type="text"
                            value={checkNumber}
                            disabled={isSaving}
                            placeholder=""
                            aria-label="Check number"
                            onChange={(e) => setCheckNumber(e.target.value)}
                            className="h-control-28px w-full rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </label>
                    <div className="flex min-w-0 flex-col gap-2">
                        <label className="flex min-w-0 flex-col gap-1">
                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Payee</span>
                            <Typeahead<PayeeSuggestion>
                                items={payees}
                                value={payee}
                                onChange={setPayee}
                                getKey={(p) => p.name}
                                getLabel={(p) => p.name}
                                disabled={isSaving}
                                aria-label="Payee"
                            />
                            <SimilarPayeesPanel
                                suggestions={similarPayees.data ?? []}
                                accountPaths={accountPaths}
                                disabled={isSaving}
                                onApply={(s) => {
                                    setPayee(s.payee);
                                    patchPosting(onlyPosting.key, {
                                        counterpartyId: s.counterpartyAccountId,
                                    });
                                }}
                            />
                            {mergePanel}
                        </label>
                        <label className="flex min-w-0 flex-col gap-1">
                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Memo</span>
                            <textarea
                                ref={headerMemoRef}
                                rows={1}
                                value={headerMemo}
                                onChange={(e) => {
                                    setHeaderMemo(e.target.value);
                                    adjustMemoHeight(e.target);
                                }}
                                onKeyDown={(e) => {
                                    if (e.key === 'Enter' && !e.shiftKey) {
                                        e.preventDefault();
                                        e.stopPropagation();
                                        handleSave();
                                    }
                                }}
                                disabled={isSaving}
                                placeholder="Optional (Shift+Enter for new line)"
                                className="min-h-control-28px max-h-fixed-96px w-full resize-none overflow-y-auto rounded border border-border bg-surface px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                            />
                        </label>
                    </div>
                    <div className="flex min-w-0 flex-col gap-2">
                        <AccountCategoryPicker
                            accounts={accounts}
                            isEligible={isEligibleCounterparty}
                            frequent={frequent}
                            valueId={onlyPosting.counterpartyId}
                            onChangeId={(id) =>
                                patchPosting(onlyPosting.key, { counterpartyId: id })}
                            label="Category"
                            placeholder="Category or account…"
                            ariaLabel="Category"
                            disabled={isSaving}
                        />
                        <TagsInput
                            tags={tags}
                            allTags={tagsQuery.data ?? []}
                            onChange={setTags}
                            disabled={isSaving}
                            aria-label="Tags"
                        />
                    </div>
                    <label className="flex min-w-0 flex-col gap-1">
                        <span className="text-right text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Amount</span>
                        <input
                            type="number"
                            step="0.01"
                            inputMode="decimal"
                            value={onlyPosting.amount}
                            placeholder="0.00"
                            disabled={isSaving}
                            aria-label="Amount"
                            onChange={(e) => patchPosting(onlyPosting.key, { amount: e.target.value })}
                            onBlur={(e) => {
                                const text = e.target.value.trim();
                                if (text.length === 0) return;
                                const n = Number(text);
                                if (!Number.isNaN(n)) patchPosting(onlyPosting.key, { amount: n.toFixed(2) });
                            }}
                            className={
                                'h-control-28px w-full rounded border border-border bg-surface px-2 text-right font-mono text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent '
                                // Same bank-register pairing as the split rows
                                // — a debit is red, a credit is plain. This
                                // field carried no colour at all, so a single
                                // transaction changed colour merely by being
                                // opened for edit.
                                + (Number(onlyPosting.amount) < 0
                                    && onlyPosting.amount.trim().length > 0
                                    && !Number.isNaN(Number(onlyPosting.amount))
                                    ? 'text-state-danger'
                                    : 'text-text')
                            }
                        />
                    </label>
                    {mode.kind === 'edit' ? (
                        <span
                            title="Pre-edit running balance"
                            className="pt-5 text-right font-mono text-xs tabular-nums text-text-subtle opacity-60"
                        >
                            {formatCurrency(mode.balanceAfter, currency)}
                        </span>
                    ) : (
                        <span aria-hidden />
                    )}
                </div>
                {/* Validation hint strip — surfaces WHY Save is
                    disabled instead of leaving the user to discover
                    via the tooltip. One issue → inline sentence;
                    multiple → comma-joined. Hidden when the form
                    is valid. */}
                {validation.length > 0 ? (
                    <div className="border-t border-state-warning/30 bg-state-warning-soft/30 px-3 py-1.5 text-[0.6875rem] text-state-warning">
                        {validation.join(' · ')}
                    </div>
                ) : null}
                <div className="flex items-center justify-between gap-2 border-t border-border/30 px-3 py-2">
                    <div className="flex items-center gap-3">
                        {footerLeading}
                        {/* Split affordance — left-aligned secondary action that
                            doesn't compete with the primary Save. Adds an empty
                            posting; the form re-renders into the multi-split
                            branch with focus on the new posting's amount. */}
                        <button
                            type="button"
                            onClick={() => addPosting()}
                            disabled={isSaving}
                            title="Add a second posting to split this transaction across categories"
                            className="text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-50"
                        >
                            Split this transaction →
                        </button>
                    </div>
                    <div className="flex gap-2">
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={onCancel}
                            disabled={isSaving}
                            title="Cancel (Esc)"
                        >
                            Cancel
                        </Button>
                        <Button
                            type="button"
                            variant="primary"
                            size="sm"
                            onClick={handleSave}
                            disabled={saveDisabled}
                            title={
                                validation.length > 0
                                    ? validation.join('\n')
                                    : saveButtonLabel
                            }
                        >
                            {saveButtonLabel}
                        </Button>
                    </div>
                </div>
            </>
        ) : (
        <>
            {/* Root row — uses the register's 8-column grid so
                Date / Check# / Payee · Memo / Amount / Balance line up
                vertically with the static register rows + column
                headers. Read columns left-to-right as: status,
                checkbox, Date, Check#, Payee/Memo (stacked with
                Tags), unused (legs put their category here below),
                Total (read-only sum), Balance (read-only). */}
            <div
                className="grid items-start gap-2 border-b border-border/40 px-3 py-2"
                style={{ gridTemplateColumns: cols }}
            >
                <span />
                <span />
                {/* Date and Tax date stack in ONE grid cell, mirroring how Memo sits
                    under Payee — so the tax date lands on the second line, aligned with
                    the memo, and Check # keeps its own column. Two grid cells here made
                    the top row too tight and truncated the tax input. */}
                <div className="flex min-w-0 flex-col gap-2">
                    <DateField
                        label="Date"
                        id={dateId}
                        value={postedAt}
                        onChange={setPostedAt}
                        disabled={isSaving}
                        autoFocus
                    />
                    <DateField
                        label="Tax date"
                        value={transactedAt}
                        onChange={setTransactedAt}
                        disabled={isSaving}
                        hint="Blank = same as posted"
                    />
                </div>
                <label className="flex min-w-0 flex-col gap-1">
                    <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Check #</span>
                    <input
                        type="text"
                        value={checkNumber}
                        disabled={isSaving}
                        placeholder=""
                        aria-label="Check number"
                        onChange={(e) => setCheckNumber(e.target.value)}
                        className="h-control-28px w-full rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    />
                </label>
                <div className="flex min-w-0 flex-col gap-2">
                    <label className="flex min-w-0 flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Payee</span>
                        <Typeahead<PayeeSuggestion>
                            items={payees}
                            value={payee}
                            onChange={setPayee}
                            getKey={(p) => p.name}
                            getLabel={(p) => p.name}
                            disabled={isSaving}
                            aria-label="Payee"
                        />
                    </label>
                    {mergePanel}
                    <label className="flex min-w-0 flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">
                            {postings.length > 1 ? 'Memo (umbrella)' : 'Memo'}
                        </span>
                        <textarea
                            ref={headerMemoRef}
                            rows={1}
                            value={headerMemo}
                            onChange={(e) => {
                                setHeaderMemo(e.target.value);
                                adjustMemoHeight(e.target);
                            }}
                            onKeyDown={(e) => {
                                if (e.key === 'Enter' && !e.shiftKey) {
                                    e.preventDefault();
                                    e.stopPropagation();
                                    handleSave();
                                }
                            }}
                            disabled={isSaving}
                            placeholder={
                                postings.length > 1
                                    ? 'Optional note that applies to the whole split (Shift+Enter for new line)'
                                    : 'Optional (Shift+Enter for new line)'
                            }
                            className="min-h-control-28px max-h-fixed-96px w-full resize-none overflow-y-auto rounded border border-border bg-surface px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </label>
                </div>
                {/* col6 (register's CATEGORY · TAGS) — umbrella
                    header-level tags (slice 2c.6b). Sits in the
                    same column as the leg-level Category below so
                    the eye reads down a single column for "what
                    this transaction is categorised as." Per
                    ADR-0009 tags live at the header level, never
                    per-leg — the umbrella label appears only on
                    multi-split rows for visual clarity. */}
                <TagsInput
                    label={postings.length > 1 ? 'Tags (umbrella)' : 'Tags'}
                    tags={tags}
                    allTags={tagsQuery.data ?? []}
                    onChange={setTags}
                    disabled={isSaving}
                    aria-label={postings.length > 1 ? 'Header-level tags' : 'Tags'}
                />
                {/* col7 (register's AMOUNT) — read-only sum of the
                    posting amounts. No "Total:" label needed (the
                    register's AMOUNT header above identifies it). */}
                <span
                    title="Sum of postings (saved as the transaction total)"
                    className="pt-5 text-right font-mono text-xs tabular-nums text-text"
                >
                    {formatCurrency(sourceTotal, currency)}
                </span>
                {/* col8 (register's BALANCE) — pre-edit reference at
                    reduced opacity. Anchors to the first input row so
                    it doesn't drift with a long memo. */}
                {mode.kind === 'edit' ? (
                    <span
                        title="Pre-edit running balance"
                        className="pt-5 text-right font-mono text-xs tabular-nums text-text-subtle opacity-60"
                    >
                        {formatCurrency(mode.balanceAfter, currency)}
                    </span>
                ) : (
                    <span aria-hidden />
                )}
            </div>

            <SplitsGrid
                postings={postings}
                issues={issues}
                accounts={accounts}
                isEligibleCounterparty={isEligibleCounterparty}
                frequent={frequent}
                currency={currency}
                total={sourceTotal}
                disabled={isSaving}
                focusKey={focusKey}
                onAutoFocused={() => setFocusKey(null)}
                onPatch={patchPosting}
                onAdd={() => addPosting()}
                onRemove={removePosting}
                onReorder={reorderPostings}
                onMove={movePosting}
            />

            {/* Bottom action row — optional leading slot left, Cancel + Save right. */}
            <div className="flex items-center justify-between gap-2 border-t border-border/30 px-3 py-2">
                <div className="flex items-center gap-3">{footerLeading}</div>
                <div className="flex gap-2">
                <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    onClick={onCancel}
                    disabled={isSaving}
                    title="Cancel (Esc)"
                >
                    Cancel
                </Button>
                <Button
                    type="button"
                    variant="primary"
                    size="sm"
                    onClick={handleSave}
                    disabled={saveDisabled}
                    title={
                        validation.length > 0
                            ? validation.join('\n')
                            : saveButtonLabel
                    }
                >
                    {saveButtonLabel}
                </Button>
                </div>
            </div>
        </>
        )}

        {saveError ? (
            <p
                role="alert"
                className="mx-3 mb-2 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-[0.6875rem] text-state-danger"
            >
                {saveError}
            </p>
        ) : null}
        </div>
    );
}
