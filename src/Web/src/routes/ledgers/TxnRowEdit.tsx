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
import { shiftDateInputValue, toDateInputValue, todayInputValue } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
import type {
    AccountSummary,
    CreateTransactionRequest,
    FrequentCounterpartiesResponse,
    MergeCandidateDto,
    PatchTransactionRequest,
    PayeeSuggestion,
    SimilarPayeeDto,
    TagDto,
    TransactionPosting,
} from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Typeahead } from '@/components/ui/Typeahead';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { TagCombobox } from '@/components/tags/TagCombobox';
import { pickableCounterparties } from '@/lib/accountPath';

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
// Affordances per posting:
//   - ⋮  drag handle (HTML5 native draggable, no library dep)
//   - amount input
//   - counterparty Typeahead
//   - leg-memo input
//   - [−] remove (disabled when only one posting remains)
//
// Add-posting affordance: a ghost row at the bottom of the list.
// Focusing any field of the ghost — by click or by Tab off the
// last real posting's memo — materialises it as a real posting
// and adds a fresh ghost below. Notion / Airtable / Linear /
// spreadsheet pattern (ADR-0023 §C extended).

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
// Posting draft state
// --------------------------------------------------------------------

interface PostingDraft {
    /** Stable id within the editor's lifecycle — used as React
     *  key during reorder. Distinct from `legId`: a freshly added
     *  posting has `legId === null` but always a `key`. */
    key: string;
    /** Existing source-side leg id (PATCH-only; null for newly
     *  added postings the server will INSERT). */
    legId: string | null;
    /** The chosen counterparty account/category id (ADR-0043 —
     *  id-based via AccountCategoryPicker; null until picked). */
    counterpartyId: string | null;
    /** Free-text amount input. Parsed on save. */
    amount: string;
    legMemo: string;
}

let postingKeyCounter = 0;
function nextKey(): string {
    postingKeyCounter += 1;
    return `p_${postingKeyCounter}`;
}

function seedToDraft(s: PostingSeed): PostingDraft {
    return {
        key: nextKey(),
        legId: s.legId,
        // Id-based (ADR-0043): the picker resolves the display name
        // from the full accounts map, so an existing system-account
        // counterparty (e.g. Uncategorized) still round-trips even
        // though it isn't offered for a fresh pick.
        counterpartyId: s.counterpartyAccountId,
        amount: s.amount.toFixed(2),
        legMemo: s.legMemo ?? '',
    };
}

function emptyDraft(): PostingDraft {
    return {
        key: nextKey(),
        legId: null,
        counterpartyId: null,
        amount: '',
        legMemo: '',
    };
}

/** Map one duplicate-prefill posting to a fresh draft (no legId). The
 *  edit path has `seedToDraft`; this is its new-mode twin. */
function prefillToDraft(p: PostingPrefill): PostingDraft {
    return {
        key: nextKey(),
        legId: null,
        counterpartyId: p.counterpartyAccountId,
        amount: p.amount.toFixed(2),
        legMemo: p.legMemo ?? '',
    };
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

    // Header field state.
    const [payee, setPayee] = useState(
        mode.kind === 'new' ? (mode.prefill?.payee ?? '') : (mode.payee ?? ''),
    );
    const [headerMemo, setHeaderMemo] = useState(
        mode.kind === 'new' ? (mode.prefill?.memo ?? '') : (mode.memo ?? ''),
    );
    const [checkNumber, setCheckNumber] = useState(
        mode.kind === 'new'
            ? (mode.prefill?.checkNumber ?? '')
            : (mode.checkNumber ?? ''),
    );
    const [postedAt, setPostedAt] = useState(
        mode.kind === 'new'
            ? (mode.postedAt ?? todayInputValue())
            : toDateInputValue(mode.postedAt),
    );

    // Slice 2c.6b: tag set. In edit mode seeds from the row's
    // current tags; in new mode starts empty. The save handler
    // sends `tags: <this list>` so the server's replace-semantics
    // produces exactly this membership.
    const [tags, setTags] = useState<readonly string[]>(
        mode.kind === 'new' ? [] : mode.tags,
    );

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

    // Posting drafts. In edit mode start from the seeds; in new
    // mode start with one empty draft (the user can add more via
    // the ghost row).
    const [postings, setPostings] = useState<PostingDraft[]>(() => {
        if (mode.kind === 'new') {
            // Duplicate seeds 1..N postings (single row = N=1, split = N);
            // the form opens with one empty posting when there's no prefill.
            const seeds = mode.prefill?.postings;
            return seeds && seeds.length > 0
                ? seeds.map((p) => prefillToDraft(p))
                : [emptyDraft()];
        }
        return mode.postings.map((s) => seedToDraft(s));
    });

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

    // Key of the most-recently-added posting. PostingRowEditor with
    // this key auto-focuses its amount input on mount; after the
    // focus lands we clear the marker so subsequent re-renders
    // don't steal focus. Drives the "click ghost row → focus
    // appears on the new row's amount" UX.
    const [focusKey, setFocusKey] = useState<string | null>(null);

    // Mutators
    function patchPosting(key: string, fields: Partial<PostingDraft>) {
        setPostings((prev) =>
            prev.map((p) => (p.key === key ? { ...p, ...fields } : p)),
        );
    }

    function addPosting(prefill?: Partial<PostingDraft>) {
        const key = nextKey();
        setPostings((prev) => [
            ...prev,
            { ...emptyDraft(), ...prefill, key, legId: null },
        ]);
        setFocusKey(key);
    }

    function removePosting(key: string) {
        setPostings((prev) =>
            prev.length === 1 ? prev : prev.filter((p) => p.key !== key),
        );
    }

    function reorderPostings(fromKey: string, toKey: string) {
        if (fromKey === toKey) return;
        setPostings((prev) => {
            const fromIdx = prev.findIndex((p) => p.key === fromKey);
            const toIdx = prev.findIndex((p) => p.key === toKey);
            if (fromIdx < 0 || toIdx < 0) return prev;
            const next = [...prev];
            const [moved] = next.splice(fromIdx, 1);
            if (moved === undefined) return prev;
            next.splice(toIdx, 0, moved);
            return next;
        });
    }

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
    const validation = useMemo(() => {
        const issues: string[] = [];
        if (postings.length === 0) issues.push('Add at least one posting.');
        for (let i = 0; i < postings.length; i++) {
            const p = postings[i]!;
            const v = Number(p.amount);
            if (p.amount.trim().length === 0 || Number.isNaN(v)) {
                issues.push(`Posting ${i + 1}: amount is required.`);
            }
            if (p.counterpartyId === null) {
                issues.push(`Posting ${i + 1}: pick a counterparty.`);
            }
        }
        return issues;
    }, [postings]);
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

        // Inverted-merge direction: when a candidate is selected,
        // the editor row is about to become a loser — its content
        // is moot. Send a minimal PATCH that just stamps the merge
        // (+ implicit approve to keep state coherent if the row is
        // ever surfaced again). The candidate stays untouched, so
        // there's nothing to apply to the editor row's payee /
        // memo / postings / tags.
        if (mergeFromHeaderId !== null) {
            return {
                kind: 'patch',
                body: {
                    mergeFromHeaderId,
                    approve: mode.needsReview ? true : undefined,
                },
            };
        }

        const body: PatchTransactionRequest = {
            payee: payee.trim().length === 0 ? null : payee.trim(),
            memo: headerMemo.trim().length === 0 ? null : headerMemo.trim(),
            checkNumber:
                checkNumber.trim().length === 0 ? null : checkNumber.trim(),
            postedAt: `${postedAt}T00:00:00.000Z`,
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
        if (event.key === 'Escape') {
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
                    <label className="flex min-w-0 flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Date</span>
                        <input
                            id={dateId}
                            type="date"
                            value={postedAt}
                            disabled={isSaving}
                            autoFocus
                            title="Date — keys: t today, y yesterday, +/- shift by day"
                            onChange={(e) => setPostedAt(e.target.value)}
                            onKeyDown={(e) => {
                                if (e.key === 't' || e.key === 'T') {
                                    e.preventDefault();
                                    setPostedAt(todayInputValue());
                                } else if (e.key === 'y' || e.key === 'Y') {
                                    e.preventDefault();
                                    setPostedAt(shiftDateInputValue(todayInputValue(), -1));
                                } else if (e.key === '+' || e.key === '=') {
                                    e.preventDefault();
                                    setPostedAt(shiftDateInputValue(postedAt, 1));
                                } else if (e.key === '-' || e.key === '_') {
                                    e.preventDefault();
                                    setPostedAt(shiftDateInputValue(postedAt, -1));
                                }
                            }}
                            className="h-7 w-full rounded border border-border bg-surface px-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </label>
                    <label className="flex min-w-0 flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Check #</span>
                        <input
                            type="text"
                            value={checkNumber}
                            disabled={isSaving}
                            placeholder=""
                            aria-label="Check number"
                            onChange={(e) => setCheckNumber(e.target.value)}
                            className="h-7 w-full rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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
                                        counterpartyId: s.categoryAccountId,
                                    });
                                }}
                            />
                            <MergeCandidatesPanel
                                candidates={mergeCandidates.data ?? []}
                                accountPaths={accountPaths}
                                selectedHeaderId={mergeFromHeaderId}
                                disabled={isSaving}
                                onSelect={(c) => {
                                    // Inverted-merge direction:
                                    // picking a candidate means "fold
                                    // this editor row INTO the
                                    // candidate." The candidate is
                                    // the surviving canonical row —
                                    // its data is preserved as-is.
                                    // The editor's form fields are
                                    // moot once a candidate is
                                    // selected (the row vanishes on
                                    // save). No pre-fill — that
                                    // would silently overwrite the
                                    // candidate's content with a
                                    // confused copy. Toggle behavior
                                    // mirrors the old direction.
                                    setMergeFromHeaderId(
                                        mergeFromHeaderId === c.headerId ? null : c.headerId);
                                }}
                            />
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
                                className="min-h-7 max-h-24 w-full resize-none overflow-y-auto rounded border border-border bg-surface px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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
                            className="h-7 w-full rounded border border-border bg-surface px-2 text-right font-mono text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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
                <label className="flex min-w-0 flex-col gap-1">
                    <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Date</span>
                    <input
                        id={dateId}
                        type="date"
                        value={postedAt}
                        disabled={isSaving}
                        autoFocus
                        title="Date — keys: t today, y yesterday, +/- shift by day"
                        onChange={(e) => setPostedAt(e.target.value)}
                        onKeyDown={(e) => {
                            if (e.key === 't' || e.key === 'T') {
                                e.preventDefault();
                                setPostedAt(todayInputValue());
                            } else if (e.key === 'y' || e.key === 'Y') {
                                e.preventDefault();
                                setPostedAt(shiftDateInputValue(todayInputValue(), -1));
                            } else if (e.key === '+' || e.key === '=') {
                                e.preventDefault();
                                setPostedAt(shiftDateInputValue(postedAt, 1));
                            } else if (e.key === '-' || e.key === '_') {
                                e.preventDefault();
                                setPostedAt(shiftDateInputValue(postedAt, -1));
                            }
                        }}
                        className="h-7 w-full rounded border border-border bg-surface px-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    />
                </label>
                <label className="flex min-w-0 flex-col gap-1">
                    <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Check #</span>
                    <input
                        type="text"
                        value={checkNumber}
                        disabled={isSaving}
                        placeholder=""
                        aria-label="Check number"
                        onChange={(e) => setCheckNumber(e.target.value)}
                        className="h-7 w-full rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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
                            className="min-h-7 max-h-24 w-full resize-none overflow-y-auto rounded border border-border bg-surface px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
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

            {/* Leg column-header strip — labels col5 (per-leg
                Memo, under root's Memo), col6 (per-leg Category +
                Tags, under root's umbrella Tags), and col7
                (Amount). Each leg field stacks directly under its
                root counterpart in the same column. */}
            <div
                aria-hidden
                className="grid gap-2 border-b border-border/30 px-3 py-1 text-[0.625rem] uppercase tracking-wider text-text-muted"
                style={{ gridTemplateColumns: cols }}
            >
                <span /><span /><span /><span />
                <span>Memo</span>
                <span>Category · Tags</span>
                <span className="text-right">Amount</span>
                <span />
            </div>

            {/* Leg rows. Drag handle in col1 (status), category +
                memo + tags stacked in col5, amount in col7, remove
                button in col8. */}
            {postings.map((p, idx) => (
                <PostingRowEditor
                    key={p.key}
                    posting={p}
                    index={idx}
                    canRemove={postings.length > 1}
                    accounts={accounts}
                    isEligibleCounterparty={isEligibleCounterparty}
                    frequent={frequent}
                    cols={cols}
                    disabled={isSaving}
                    autoFocusAmount={p.key === focusKey}
                    onAutoFocused={() => setFocusKey(null)}
                    onChange={(fields) => patchPosting(p.key, fields)}
                    onRemove={() => removePosting(p.key)}
                    onReorder={reorderPostings}
                />
            ))}
            {/* Ghost click-target — one click materialises a new
                posting + auto-focuses its amount input. */}
            <GhostPostingRow
                cols={cols}
                disabled={isSaving}
                onMaterialise={() => addPosting()}
            />

            {/* Validation hint strip — same contract as the single-
                posting branch. */}
            {validation.length > 0 ? (
                <div className="border-t border-state-warning/30 bg-state-warning-soft/30 px-3 py-1.5 text-[0.6875rem] text-state-warning">
                    {validation.join(' · ')}
                </div>
            ) : null}

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

// --------------------------------------------------------------------
// PostingRowEditor + GhostPostingRow
// --------------------------------------------------------------------

interface PostingRowEditorProps {
    posting: PostingDraft;
    index: number;
    canRemove: boolean;
    /** Full ledger accounts (for the picker's path/display) + the
     *  eligibility predicate + the source account's frequents —
     *  ADR-0043, same shared picker as the investment editor. */
    accounts: readonly AccountSummary[];
    isEligibleCounterparty: (a: AccountSummary) => boolean;
    frequent: FrequentCounterpartiesResponse | null;
    /** Register's 8-column grid template — leg rows ride on the
     *  same template so col5 (category + memo + tags) and col7
     *  (amount) align with the register's PAYEE/MEMO and AMOUNT
     *  columns above. */
    cols: string;
    disabled: boolean;
    /** True for the most-recently-added posting; the amount input
     *  focuses itself on mount so the user can start typing
     *  immediately after clicking the ghost row. */
    autoFocusAmount: boolean;
    /** Called once after autoFocusAmount-driven focus lands, so
     *  the parent can clear its focusKey marker. */
    onAutoFocused: () => void;
    onChange: (fields: Partial<PostingDraft>) => void;
    onRemove: () => void;
    onReorder: (fromKey: string, toKey: string) => void;
}

function PostingRowEditor({
    posting,
    index,
    canRemove,
    accounts,
    isEligibleCounterparty,
    frequent,
    cols,
    disabled,
    autoFocusAmount,
    onAutoFocused,
    onChange,
    onRemove,
    onReorder,
}: PostingRowEditorProps) {
    const [dragOver, setDragOver] = useState(false);
    const amountRef = useRef<HTMLInputElement | null>(null);
    const memoRef = useRef<HTMLTextAreaElement | null>(null);
    useEffect(() => {
        if (autoFocusAmount && amountRef.current) {
            amountRef.current.focus();
            onAutoFocused();
        }
    }, [autoFocusAmount, onAutoFocused]);
    function adjustMemoHeight(el: HTMLTextAreaElement | null) {
        if (el === null) return;
        el.style.height = 'auto';
        const maxPx = 96;
        el.style.height = `${Math.min(el.scrollHeight, maxPx)}px`;
    }
    useEffect(() => {
        adjustMemoHeight(memoRef.current);
    }, [posting.legMemo]);
    return (
        <div
            className={
                'grid items-start gap-2 border-b border-border/20 px-3 py-1 ' +
                (dragOver
                    ? 'bg-accent-soft/30 shadow-[inset_2px_0_0_var(--color-accent)]'
                    : '')
            }
            style={{ gridTemplateColumns: cols }}
            onDragOver={(e) => {
                e.preventDefault();
                setDragOver(true);
            }}
            onDragLeave={() => setDragOver(false)}
            onDrop={(e) => {
                e.preventDefault();
                setDragOver(false);
                const fromKey = e.dataTransfer.getData('text/posting-key');
                if (fromKey.length > 0) onReorder(fromKey, posting.key);
            }}
        >
            {/* col1-3 (status / checkbox / date) — empty on leg
                rows. */}
            <span /><span /><span />
            {/* col4 (register's CHECK# column) — drag handle. Sits
                immediately to the left of the leg memo so the
                affordance is right next to the field a user grabs
                when they want to move the row. */}
            <span
                role="button"
                aria-label={`Reorder posting ${index + 1}`}
                title="Drag to reorder"
                draggable={!disabled && canRemove}
                onDragStart={(e) => {
                    e.dataTransfer.setData('text/posting-key', posting.key);
                    e.dataTransfer.effectAllowed = 'move';
                }}
                className="cursor-move select-none self-start pt-1 text-center text-text-subtle hover:text-text"
            >
                ⋮
            </span>
            {/* col5 (register's PAYEE · MEMO) — per-leg memo,
                stacked directly under the root's Memo input above.
                Disabled when the transaction has a single posting:
                the umbrella memo above IS the only memo a
                single-row txn needs, and a per-leg memo on the
                only leg would be redundant. Existing leg memo
                content (e.g. from a previous multi-split that's
                been collapsed to a single posting) is preserved
                read-only, not silently dropped. */}
            <textarea
                ref={memoRef}
                rows={1}
                value={posting.legMemo}
                disabled={disabled || !canRemove}
                placeholder={
                    canRemove
                        ? 'Posting memo (optional)'
                        : 'Use the umbrella memo above'
                }
                title={
                    canRemove
                        ? undefined
                        : 'Per-leg memo applies to multi-split transactions; for a single posting the umbrella memo above is the canonical memo.'
                }
                aria-label={`Posting ${index + 1} memo`}
                onChange={(e) => {
                    onChange({ legMemo: e.target.value });
                    adjustMemoHeight(e.target);
                }}
                onKeyDown={(e) => {
                    if (e.key === 'Enter' && e.shiftKey) {
                        e.stopPropagation();
                    }
                }}
                className="min-h-7 max-h-24 w-full resize-none overflow-y-auto rounded border border-border bg-surface px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50"
            />
            {/* col6 (register's CATEGORY · TAGS) — category
                typeahead + per-leg tags placeholder, stacked under
                the root's Tags umbrella slot above. Reading down
                the column: "umbrella tags → leg category → leg
                tags" tracks one categorisation hierarchy. */}
            <div className="flex min-w-0 flex-col gap-1">
                <AccountCategoryPicker
                    accounts={accounts}
                    isEligible={isEligibleCounterparty}
                    frequent={frequent}
                    valueId={posting.counterpartyId}
                    onChangeId={(id) => onChange({ counterpartyId: id })}
                    placeholder="Category or account…"
                    ariaLabel={`Posting ${index + 1} category`}
                    disabled={disabled}
                />
                <TagsPlaceholder
                    hint="Per-posting tags coming soon"
                    aria-label={`Posting ${index + 1} tags`}
                />
            </div>
            {/* col7 (register's AMOUNT) — leg amount input. */}
            <input
                ref={amountRef}
                type="number"
                step="0.01"
                inputMode="decimal"
                value={posting.amount}
                placeholder="0.00"
                disabled={disabled}
                aria-label={`Posting ${index + 1} amount`}
                onChange={(e) => onChange({ amount: e.target.value })}
                onBlur={(e) => {
                    const text = e.target.value.trim();
                    if (text.length === 0) return;
                    const n = Number(text);
                    if (!Number.isNaN(n)) onChange({ amount: n.toFixed(2) });
                }}
                className="h-7 w-full rounded border border-border bg-surface px-2 text-right font-mono text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            />
            {/* col8 (register's BALANCE) — remove button. The
                register's balance is meaningless mid-edit (the
                row is being mutated), so the slot hosts the
                remove affordance. */}
            <button
                type="button"
                aria-label={`Remove posting ${index + 1}`}
                title={
                    canRemove
                        ? 'Remove this posting'
                        : 'Cannot remove — a transaction needs at least one posting'
                }
                disabled={disabled || !canRemove}
                onClick={onRemove}
                className="ml-auto h-7 w-7 rounded text-text-subtle hover:bg-state-danger-soft hover:text-state-danger disabled:cursor-not-allowed disabled:opacity-30"
            >
                −
            </button>
        </div>
    );
}

interface GhostPostingRowProps {
    cols: string;
    disabled: boolean;
    onMaterialise: () => void;
}

/** Ghost row affordance (ADR-0025): clicking anywhere on this
 *  row materialises a new posting and moves focus to the new
 *  row's amount input (handled by the parent via
 *  `autoFocusAmount` on the freshly-added PostingRowEditor).
 *
 *  Implemented as a single click-target button (not real inputs)
 *  so the materialise fires exactly once per interaction. */
function GhostPostingRow({
    cols,
    disabled,
    onMaterialise,
}: GhostPostingRowProps) {
    return (
        <button
            type="button"
            disabled={disabled}
            onClick={onMaterialise}
            aria-label="Add another posting"
            title="Add another posting"
            className="grid w-full items-center gap-2 border-b border-dashed border-border/40 bg-transparent px-3 py-1 text-left italic text-text-subtle opacity-60 transition-opacity hover:opacity-100 focus-visible:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            style={{ gridTemplateColumns: cols }}
        >
            <span /><span /><span />
            <span aria-hidden className="text-center not-italic">⋮</span>
            <span aria-hidden className="px-2 text-xs">
                + Add another posting…
            </span>
            <span /><span /><span />
        </button>
    );
}

// --------------------------------------------------------------------
// TagsPlaceholder
// --------------------------------------------------------------------

/** Reserved layout slot for tag editing — visual placeholder, not
 *  a real input. Keeps the form layout stable so a future PR
 *  adding tag editing doesn't reshuffle the editor (user: "I DO
 *  NOT WANT TO REDESIGN FORMS IN EVERY PR"). */
function TagsPlaceholder({
    label,
    hint,
    'aria-label': ariaLabel,
}: {
    label?: string;
    hint: string;
    'aria-label'?: string;
}) {
    return (
        <label
            className="flex min-w-0 flex-col gap-1 text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted"
            aria-label={ariaLabel}
        >
            {label !== undefined ? <span>{label}</span> : null}
            <div
                role="presentation"
                title={hint}
                className="flex h-7 min-w-0 items-center rounded border border-dashed border-border bg-transparent px-2 text-xs italic text-text-subtle opacity-50"
            >
                {hint}
            </div>
        </label>
    );
}

// --------------------------------------------------------------------
// TagsInput (slice 2c.6b; Tags v1 autocomplete)
// --------------------------------------------------------------------
// Chip-style header-level tag editor. Applied tags render as inline
// chips with an × to remove; the trailing field is a shared
// {@link TagCombobox} that autocompletes against the ledger's tag
// dictionary (colour swatch + usage) and offers "Create '<new>'" for a
// fresh name. Tag matching is case-insensitive within the ledger (server
// enforces) so the SPA dedupes against applied chips with a lower-case
// key. Constraints mirror the server validation (BusinessError codes
// `transaction-tag-{empty,too-long}` and `transaction-tags-too-many`):
// names are trimmed, whitespace-only is rejected, and the field is
// disabled once the cap is reached.

const TAG_MAX_LENGTH = 64;
const TAG_MAX_COUNT = 20;

function TagsInput({
    label,
    tags,
    allTags,
    onChange,
    disabled,
    'aria-label': ariaLabel,
}: {
    label?: string;
    /** Tag names currently applied to this transaction. */
    tags: readonly string[];
    /** The ledger's tag dictionary — powers the autocomplete. */
    allTags: readonly TagDto[];
    onChange: (next: readonly string[]) => void;
    disabled: boolean;
    'aria-label'?: string;
}) {
    // Add a name chosen from the combobox (an existing tag or a freshly
    // typed one): dedupe case-insensitively + honour the same length /
    // count caps the server enforces.
    const addName = (name: string) => {
        const trimmed = name.trim();
        if (trimmed.length === 0 || trimmed.length > TAG_MAX_LENGTH) return;
        const lower = trimmed.toLowerCase();
        if (tags.some((t) => t.toLowerCase() === lower)) return;
        if (tags.length >= TAG_MAX_COUNT) return;
        onChange([...tags, trimmed]);
    };

    const removeAt = (index: number) => onChange(tags.filter((_, i) => i !== index));

    const atCap = tags.length >= TAG_MAX_COUNT;

    return (
        <label
            className="flex min-w-0 flex-col gap-1 text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted"
            aria-label={ariaLabel}
        >
            {label !== undefined ? <span>{label}</span> : null}
            <div
                className="flex min-h-7 min-w-0 flex-wrap items-center gap-1 rounded border border-border bg-surface px-1 py-0.5 text-xs focus-within:outline-none focus-within:ring-2 focus-within:ring-accent"
            >
                {tags.map((tag, i) => (
                    <span
                        key={`${tag.toLowerCase()}_${i}`}
                        className="inline-flex items-center gap-1 rounded bg-surface-muted px-1.5 py-0.5 text-[0.6875rem] text-text"
                    >
                        {tag}
                        <button
                            type="button"
                            onClick={() => removeAt(i)}
                            disabled={disabled}
                            aria-label={`Remove tag ${tag}`}
                            className="text-text-subtle hover:text-state-danger focus-visible:text-state-danger focus-visible:outline-none"
                        >
                            ×
                        </button>
                    </span>
                ))}
                <TagCombobox
                    tags={allTags}
                    excludeNames={tags}
                    onCommit={addName}
                    onBackspaceEmpty={() => { if (tags.length > 0) onChange(tags.slice(0, -1)); }}
                    disabled={disabled || atCap}
                    placeholder={tags.length === 0 ? 'Add tag…' : atCap ? '' : '+ tag'}
                    aria-label="Add tag"
                    maxLength={TAG_MAX_LENGTH}
                    inputClassName="min-w-[3rem] flex-1 border-none bg-transparent px-1 text-xs focus:outline-none disabled:cursor-not-allowed"
                />
            </div>
        </label>
    );
}

// --------------------------------------------------------------------
// SimilarPayeesPanel (slice 2c.6c)
// --------------------------------------------------------------------
// Inline chip row beneath the payee input in the single-posting edit
// template. Static at row-open (per design — no typeahead refetch).
// Renders nothing when there are no suggestions (server returns []
// for non-bank-feed rows, missing payees, or no matches). Clicking a
// chip applies its (payee, category) pair to the form draft —
// counterparty path resolution goes through the editor's existing
// accountPaths lookup so the Typeahead's display matches the format
// the rest of the form expects.

function SimilarPayeesPanel({
    suggestions,
    accountPaths,
    disabled,
    onApply,
}: {
    suggestions: readonly SimilarPayeeDto[];
    accountPaths: Map<string, string>;
    disabled: boolean;
    onApply: (suggestion: SimilarPayeeDto) => void;
}) {
    if (suggestions.length === 0) return null;
    return (
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-1.5 gap-y-1 pt-0.5 text-[0.625rem]">
            <span className="text-text-subtle">Similar:</span>
            {suggestions.map((s) => {
                const categoryLabel =
                    accountPaths.get(s.categoryAccountId) ?? s.categoryAccountName;
                return (
                    <button
                        key={`${s.payee}::${s.categoryAccountId}`}
                        type="button"
                        disabled={disabled}
                        onClick={() => onApply(s)}
                        title={`Apply payee "${s.payee}" + category "${categoryLabel}" (used ${s.useCount}×)`}
                        className="inline-flex items-baseline gap-1 rounded border border-border bg-surface px-1.5 py-0.5 text-text hover:border-accent hover:bg-surface-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50"
                    >
                        <span className="font-medium">{s.payee}</span>
                        <span className="text-text-subtle">→</span>
                        <span>{categoryLabel}</span>
                        {s.useCount > 1 ? (
                            <span className="text-text-subtle">×{s.useCount}</span>
                        ) : null}
                    </button>
                );
            })}
        </div>
    );
}

// --------------------------------------------------------------------
// MergeCandidatesPanel (slice 2c.6d)
// --------------------------------------------------------------------
// "Possible matches" chip row beneath the payee field. Each chip
// represents a manual row whose source-account aggregate equals
// this row's, within ±7 days. Clicking pre-fills the editor with
// the candidate's payee / memo / tags / postings AND arms
// `mergeFromHeaderId` for the next save (the saving handler sends
// it in the PATCH body so the server stamps `is_merged_into` on
// the manual row in the same transaction).
//
// Selection is a toggle: re-clicking the active chip clears the
// merge arm (keeping the form state) — provides a "cancel merge,
// keep my edits" path without restoring original state.

function MergeCandidatesPanel({
    candidates,
    accountPaths,
    selectedHeaderId,
    disabled,
    onSelect,
}: {
    candidates: readonly MergeCandidateDto[];
    accountPaths: Map<string, string>;
    selectedHeaderId: string | null;
    disabled: boolean;
    onSelect: (candidate: MergeCandidateDto) => void;
}) {
    if (candidates.length === 0) return null;
    return (
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-1.5 gap-y-1 pt-0.5 text-[0.625rem]">
            <span className="text-text-subtle">Merge candidates:</span>
            {candidates.map((c) => {
                const isSelected = selectedHeaderId === c.headerId;
                // postedAt arrives UTC-anchored (server treats it as
                // a calendar date); slice the date portion directly
                // — round-tripping through new Date(...).toISOString()
                // is equivalent but slower.
                const dateLabel = c.postedAt.slice(0, 10);
                const summary =
                    c.postings.length === 1
                        ? accountPaths.get(c.postings[0]!.counterpartyAccountId)
                          ?? c.postings[0]!.counterpartyAccountName
                        : `${c.postings.length} splits`;
                return (
                    <button
                        key={c.headerId}
                        type="button"
                        disabled={disabled}
                        onClick={() => onSelect(c)}
                        aria-pressed={isSelected}
                        title={
                            isSelected
                                ? 'Click to cancel the merge (form edits stay).'
                                : `Merge with ${c.payee ?? '(no payee)'} (${dateLabel}). The bank row keeps its identity; the manual row is marked as merged.`
                        }
                        className={
                            'inline-flex items-baseline gap-1 rounded border px-1.5 py-0.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 ' +
                            (isSelected
                                ? 'border-accent bg-accent-soft text-accent'
                                : 'border-border bg-surface text-text hover:border-accent hover:bg-surface-muted')
                        }
                    >
                        <span className="text-text-subtle">{dateLabel}</span>
                        <span className="font-medium">
                            {c.payee ?? '(no payee)'}
                        </span>
                        <span className="text-text-subtle">→</span>
                        <span>{summary}</span>
                        {isSelected ? (
                            <span className="text-text-subtle" aria-hidden>✓</span>
                        ) : null}
                    </button>
                );
            })}
        </div>
    );
}
