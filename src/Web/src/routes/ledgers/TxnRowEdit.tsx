import {
    useCallback,
    useEffect,
    useId,
    useLayoutEffect,
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
import { formatCurrency, roundToMinorUnits } from '@/lib/money';
import { MoneyInput } from '@/components/ui/MoneyInput';
import { cn } from '@/lib/cn';
import { fitTextareaHeight, refitOnWidthChange } from '@/lib/fitTextareaHeight';
import type {
    AccountSummary,
    CreateTransactionRequest,
    FrequentCounterpartiesResponse,
    PatchTransactionRequest,
    PayeeSuggestion,
    RateUnavailable,
    TransactionPosting,
} from '@/lib/types';
import { rowBalance } from './register/strategies/registerMoney';
import { Mixed } from './home/Money';
import { Button } from '@/components/ui/Button';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { FieldSizeContext, NARROW_DIALOG_FIELD } from '@/components/ui/fieldSize';
import { pendingTextProps, usePendingButtonProps } from '@/components/ui/pending';
import { happenedInside } from '@/components/ui/popoverDismissal';
import { Typeahead } from '@/components/ui/Typeahead';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { pickableCounterparties } from '@/lib/accountPath';
import { counterpartyWords, type CounterpartyKind } from '@/lib/counterparty';
import { postingIssues, validatePostings } from './bank-edit/validation';
import { useTxnRowDraft } from './bank-edit/hooks/useTxnRowDraft';
import { seedIsCreate, seedNotesBaseline } from './bank-edit/draft';
import { buildNotes, collapseNotes, notesIssues } from './bank-edit/notesBody';
import { inheritedAfterSave } from './bank-edit/inheritedAfterSave';
import { NotesFieldCaption } from './bank-edit/fields/NotesFieldCaption';
import {
    normalizeMemo,
    tagCountCeiling,
    unionTags,
    type InheritedNotes,
} from '@/lib/annotations/notes';
import { InheritedMarker } from '@/components/register/InheritedMarker';
import { TagSelect } from '@/components/tags/TagSelect';
import { SimilarPayeesPanel } from './bank-edit/fields/SimilarPayeesPanel';
import { MergeCandidatesPanel } from './bank-edit/fields/MergeCandidatesPanel';
import { SplitsGrid } from './bank-edit/fields/SplitsGrid';
import { postingCurrency } from './bank-edit/postingCurrency';
import type { PostingDraft } from './bank-edit/postingDraft';
import { secondAmountSide, secondAmountToSend } from './bank-edit/secondAmount';
import { OneLineSecondAmount } from './bank-edit/fields/SecondAmountField';
import type { SecondAmountSide } from '@/lib/secondAmount';
import { saveErrorWords, saveRefusal } from './saveErrorWords';

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

/** A single posting to seed a NEW transaction with (Duplicate path). */
export interface PostingPrefill {
    /** The stored line's source leg, read only when the prefill IS the stored
     *  thing being edited (`prefillIsStored`, a reminder template). A copy is
     *  always a fresh leg, so this is ignored for it. */
    legId?: string | null;
    counterpartyAccountId: string | null;
    amount: number;
    /** The line's memo on a split. A one-posting prefill carries its memo in
     *  the prefill's `memo`; a stored one (a reminder template) may also carry
     *  it here, as what a split copies onto this line. */
    legMemo?: string | null;
    /** The line's own tags (ADR-0103) on a split, carried through a Duplicate.
     *  On a stored one-posting prefill, like `legMemo`: the copy a split puts
     *  on this line. */
    legTags?: readonly string[];
    /** The counterparty leg's own figure, signed, on a transfer between real accounts in
     *  different currencies (ADR-0114 D13): Duplicate and a reminder template carry both
     *  amounts, and the editor never refills a copied one. Null or absent otherwise. */
    counterpartyAmount?: number | null;
}

/** Optional pre-fill for the new-transaction form (Duplicate path).
 *  Header fields + the postings to clone. A single-row duplicate seeds
 *  ONE posting; a split duplicate seeds N — the SAME path: the editor
 *  maps each prefill posting to a fresh draft. posted_at intentionally
 *  defaults to today.
 *
 *  `memo` / `tags` are the new transaction's: this side's on one posting,
 *  the whole transaction's on several. */
export interface TxnRowNewPrefill {
    payee?: string | null;
    memo?: string | null;
    tags?: readonly string[];
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
    /** The line's own memo on a split; null on a one-posting transaction,
     *  whose memo is the Memo field. */
    legMemo: string | null;
    /** The line's own tags on a split (ADR-0103); empty on a one-posting
     *  transaction. Seeded so an edit shows them; without it every save
     *  would look like a deliberate clear. */
    legTags: readonly string[];
    /** The counterparty leg's own figure, signed, on a transfer between real accounts in
     *  different currencies (ADR-0114 D13); null on a mirrored pair. A saved transaction
     *  is never refilled: both amounts are seeded from what was stored (ADR-0114 D14). */
    counterpartyAmount?: number | null;
}


export type TxnRowMode =
    | {
          kind: 'new';
          sourceAccountId: string;
          prefill?: TxnRowNewPrefill;
          /** The prefill is a stored thing being edited through the create
           *  form (a reminder's template), not a copy: the transaction's memo
           *  and tags are then sent only when they differ from it, exactly as
           *  an edit sends them, and each line restates its own, because the
           *  server rebuilds the lines (see bank-edit/notesBody.ts). Omitted /
           *  false: a create, which sends every non-blank value. */
          prefillIsStored?: boolean;
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
          /** The payee + memo + posted_at to seed the form's header
           *  fields. `memo` is THIS side's own on a one-split (blank
           *  while it inherits) and the transaction's on a split. */
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
          /** The pre-edit running balance, as the register row shows it: in
           *  `balanceCurrencyCode`, which on a category is the reporting currency, not
           *  the category's own, and null with `balanceRateUnavailable` named when it
           *  needs a missing rate (ADR-0114 D7, D9). */
          balanceAfter: number | null;
          balanceCurrencyCode: string;
          balanceRateUnavailable?: RateUnavailable[];
          /** The tags the Tags field seeds from: this side's own on a
           *  one-split (empty while it inherits), the transaction's on a
           *  split. A save sends them only when the user changed them. */
          tags: readonly string[];
          /** REQUIRED, null when nothing can be inherited, for the same
           *  reason as `transactedAt`: a caller that forgot it would open
           *  an inheriting side with no hint of what it shows. */
          inherited: InheritedNotes | null;
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

/**
 * Where the editor is hosted, and so how it lays its fields out.
 *
 *   * `register` (the default): one row of the register, on the register's own
 *     grid template (`cols`), so its fields sit under the register's columns,
 *     with the pre-edit balance in the balance column.
 *   * `dialog`: inside an EditorDialog (Post or skip, the reminder series
 *     editor, Review). Five columns of its own — Date · Check # · Payee ·
 *     Category · Amount, with Tax date, Memo and Tags beneath — and none of
 *     the register's own tracks. Below the dialog's `@3xl/dialog` width it
 *     stacks one field per row (Date beside Check #) at touch size, and the
 *     footer's Cancel and primary become 44px buttons side by side.
 */
export type TxnRowEditLayoutProps =
    | {
          layout?: 'register';
          /** Register grid template (8 columns, same as the register's
           *  `BANK_COLS`). The editor uses the same template so root + leg
           *  fields align vertically with the register's static rows and the
           *  column-header strip above. */
          cols: string;
      }
    | { layout: 'dialog'; cols?: never };

export type TxnRowEditProps = TxnRowEditCommonProps & TxnRowEditLayoutProps;

interface TxnRowEditCommonProps {
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
    /**
     * The source posting's currency: the register account's, and on a category's
     * register the row's own, the currency of the account that paid it (ADR-0114 D7).
     * Amounts are seeded, typed and rounded at its places, and a split's running total
     * prints in it (ADR-0114 D14, D15). On a category's register a posting whose paying
     * account is picked takes that account's currency instead (bank-edit/postingCurrency.ts).
     */
    currency: string;
    /** Fire a patch (edit mode). */
    onSavePatch?: (body: PatchTransactionRequest) => void;
    /** Fire a create (new mode). */
    onSaveCreate?: (body: CreateTransactionRequest) => void;
    onCancel: () => void;
    isSaving: boolean;
    /** The last save's refusal: words, or the server's refusal itself, which the editor
     *  puts in words it can say better than the server's detail (see
     *  `saveErrorWords`). */
    saveError: string | ApiError | null;
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
    /** Whether the request in flight is this editor's own save, so Save reads
     *  its busy label. A host whose other request also holds the editor (the
     *  reminders dialog's Skip) passes its save's own pending state here, and
     *  Save reads its resting label while the other request is in flight.
     *  Defaults to `isSaving`. */
    submitting?: boolean;
    /** Offer the Similar and Merge candidates strips under Payee (edit mode).
     *  Defaults to true. The home screen's Review turns them off for a row
     *  that is no longer awaiting review, which saves as a plain edit. */
    hints?: boolean;
    /** Told the one-posting transfer's other account while it is a real account in
     *  another currency, and null otherwise (a split, a category, the same currency):
     *  the reminder series editor's Schedule says where an estimate's other side comes
     *  from (ADR-0114 D13). */
    onSecondAmountSideChange?: (side: SecondAmountSide | null) => void;
    /**
     * The Review editor's rounded-at-import note, "Rounded from <bank figure> as sent by
     * the bank" (ADR-0114 D15, board RV-2): Amount's description, muted under it. Only
     * the home screen's Review passes it, and only the dialog layout's one-posting form
     * draws it; a split has no one Amount for it to describe.
     */
    amountNote?: string | null;
}

// The header grid, shared by the one-split and split layouts. In the register
// its columns are the register's (in a dialog, DIALOG_GRID's five); its rows
// are four, and every cell names its own:
//
//   1  fields     Date · Check # · Payee · Category (empty on a split) · Amount
//   2  hints      the Similar / Merge candidates strips, under Payee only
//   3  captions   Tax date · Memo · Tags
//   4  notes      their controls
//
// Row 2 also holds, right of the hints, a second amount's rate line (ADR-0114
// D14) or the rounded-at-import note under Amount (ADR-0114 D15). A wide dialog
// whose one posting carries both adds a fifth row: the note keeps row 2, the
// rate line moves to row 3, the hints span rows 2 and 3, and the captions and
// controls move to rows 4 and 5 (DIALOG_SLOT_SECOND_NOTED, SECOND_SLOT's
// dialogNoted). A narrow dialog lays one field per row (DIALOG_SLOT).
//
// The hints get a row of their own so that whatever shows under Payee moves
// every column's notes down together. Inside the Payee cell they would push
// Memo down and leave Tags where it is, out of step by the hints' height. The
// row collapses
// when there is no hint (`empty:hidden`), so it costs nothing then.
//
// Captions and controls are separate rows, captions bottom-aligned, so a
// caption that wraps (Memo (whole transaction) is wider than the Payee column
// at its floor) lifts its own first line instead of dropping its control below
// its neighbours'. Spacing is padding, not a row gap: a gap would also open
// around the empty hints row.
const ROW = {
    fields: 'row-start-1',
    hints: 'row-start-2 pt-1',
    captions: 'row-start-3 self-end pt-2',
    notes: 'row-start-4 pt-1',
} as const;
const COL = {
    date: 'col-start-3',
    check: 'col-start-4',
    payee: 'col-start-5',
    category: 'col-start-6',
    amount: 'col-start-7',
    balance: 'col-start-8',
} as const;

/** Every cell of the header grid, by what it holds. */
type Slot =
    | 'date' | 'check' | 'payee' | 'category' | 'amount' | 'hints'
    | 'taxCaption' | 'taxControl' | 'memoCaption' | 'memoControl'
    | 'tagsCaption' | 'tagsControl';

/** The register layout: ROW × COL, on the register's own template. */
const REGISTER_SLOT: Record<Slot, string> = {
    date: `${ROW.fields} ${COL.date}`,
    check: `${ROW.fields} ${COL.check}`,
    payee: `${ROW.fields} ${COL.payee}`,
    category: `${ROW.fields} ${COL.category}`,
    amount: `${ROW.fields} ${COL.amount}`,
    hints: `${ROW.hints} ${COL.payee}`,
    taxCaption: `${ROW.captions} ${COL.date}`,
    taxControl: `${ROW.notes} ${COL.date}`,
    memoCaption: `${ROW.captions} ${COL.payee}`,
    memoControl: `${ROW.notes} ${COL.payee}`,
    tagsCaption: `${ROW.captions} ${COL.category}`,
    tagsControl: `${ROW.notes} ${COL.category}`,
};

/**
 * The dialog layout's template: Date · Check # · Payee · Category · Amount
 * where five columns fit; below `@3xl/dialog`, Date beside Check # and every
 * other field across both.
 */
const DIALOG_GRID =
    'grid-cols-[7.5rem_4.75rem_minmax(0,1fr)_minmax(0,1fr)_7.5rem] @max-3xl/dialog:grid-cols-[minmax(0,1fr)_6rem]';

/**
 * The dialog layout's cells. Wide, the register's four rows on five columns:
 * the fields, the hints under Payee, then Tax date, Memo and Tags beneath Date,
 * Payee and Category. Narrow, one field per row in reading order: Date and
 * Check #, Payee, the hints, Category, Amount, then each note's caption over
 * its control. Spacing is padding for the reason ROW gives.
 */
const DIALOG_SLOT: Record<Slot, string> = {
    date: 'row-start-1 col-start-1',
    check: 'row-start-1 col-start-2',
    payee: 'row-start-1 col-start-3 @max-3xl/dialog:row-start-2 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    category: 'row-start-1 col-start-4 @max-3xl/dialog:row-start-4 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    amount: 'row-start-1 col-start-5 @max-3xl/dialog:row-start-5 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    hints: 'row-start-2 col-start-3 pt-1 @max-3xl/dialog:row-start-3 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-2',
    taxCaption: 'row-start-3 col-start-1 self-end pt-2 @max-3xl/dialog:row-start-6 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    taxControl: 'row-start-4 col-start-1 pt-1 @max-3xl/dialog:row-start-7 @max-3xl/dialog:col-span-full',
    memoCaption: 'row-start-3 col-start-3 self-end pt-2 @max-3xl/dialog:row-start-8 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    memoControl: 'row-start-4 col-start-3 pt-1 @max-3xl/dialog:row-start-9 @max-3xl/dialog:col-span-full',
    tagsCaption: 'row-start-3 col-start-4 self-end pt-2 @max-3xl/dialog:row-start-10 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    tagsControl: 'row-start-4 col-start-4 pt-1 @max-3xl/dialog:row-start-11 @max-3xl/dialog:col-span-full',
};

/**
 * The dialog layout while the one posting carries a second amount (ADR-0114 D14, the
 * ADR-0025 2026-11-14 amendment): one more 7.5rem track right of Amount, which Payee and
 * Category give up the width for. Both are static literals, so the build sees them.
 */
const DIALOG_GRID_SECOND =
    'grid-cols-[7.5rem_4.75rem_minmax(0,1fr)_minmax(0,1fr)_7.5rem_7.5rem] @max-3xl/dialog:grid-cols-[minmax(0,1fr)_6rem]';

/**
 * DIALOG_SLOT with the second amount: wide, its fields row is unchanged and the notes
 * keep their columns; narrow, the second amount takes the row after Amount and its rate
 * line the row after that, and the notes move down by those two.
 */
const DIALOG_SLOT_SECOND: Record<Slot, string> = {
    ...DIALOG_SLOT,
    taxCaption: 'row-start-3 col-start-1 self-end pt-2 @max-3xl/dialog:row-start-8 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    taxControl: 'row-start-4 col-start-1 pt-1 @max-3xl/dialog:row-start-9 @max-3xl/dialog:col-span-full',
    memoCaption: 'row-start-3 col-start-3 self-end pt-2 @max-3xl/dialog:row-start-10 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    memoControl: 'row-start-4 col-start-3 pt-1 @max-3xl/dialog:row-start-11 @max-3xl/dialog:col-span-full',
    tagsCaption: 'row-start-3 col-start-4 self-end pt-2 @max-3xl/dialog:row-start-12 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    tagsControl: 'row-start-4 col-start-4 pt-1 @max-3xl/dialog:row-start-13 @max-3xl/dialog:col-span-full',
};

/**
 * DIALOG_SLOT with the second amount and the rounded-at-import note (ADR-0114 D15): the
 * note takes the line under Amount, so the rate line moves down one, and both end at the
 * second amount's right edge (ROUNDED_NOTE_SLOT_SECOND). Wide, the rate line
 * is on row 3 and the notes on rows 4 and 5, and the hints span rows 2 and 3, so they
 * stay under Payee and the rate line stays under the note. Narrow, the note is on the row
 * after Amount, the second amount and its rate line on the next two, and the notes move
 * down past all three. DIALOG_ROWS_SECOND_NOTED goes with it.
 */
const DIALOG_SLOT_SECOND_NOTED: Record<Slot, string> = {
    ...DIALOG_SLOT,
    hints: 'row-start-2 row-span-2 col-start-3 pt-1 @max-3xl/dialog:row-start-3 @max-3xl/dialog:row-span-1 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-2',
    taxCaption: 'row-start-4 col-start-1 self-end pt-2 @max-3xl/dialog:row-start-9 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    taxControl: 'row-start-5 col-start-1 pt-1 @max-3xl/dialog:row-start-10 @max-3xl/dialog:col-span-full',
    memoCaption: 'row-start-4 col-start-3 self-end pt-2 @max-3xl/dialog:row-start-11 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    memoControl: 'row-start-5 col-start-3 pt-1 @max-3xl/dialog:row-start-12 @max-3xl/dialog:col-span-full',
    tagsCaption: 'row-start-4 col-start-4 self-end pt-2 @max-3xl/dialog:row-start-13 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
    tagsControl: 'row-start-5 col-start-4 pt-1 @max-3xl/dialog:row-start-14 @max-3xl/dialog:col-span-full',
};

/**
 * The wide rows of DIALOG_SLOT_SECOND_NOTED. Hints taller than the note and the rate line
 * would otherwise share their extra height between rows 2 and 3, opening a gap between
 * the note and the rate line; row 3 flexes, so it takes all of it. Narrow, one field per
 * row needs no template.
 */
const DIALOG_ROWS_SECOND_NOTED = 'grid-rows-[auto_auto_1fr_auto_auto] @max-3xl/dialog:grid-rows-none';

/** Where the second amount and its rate line sit: in the register on the Balance track,
 *  the line ending under the field; in a dialog on the sixth track, the line one row
 *  lower while the rounded-at-import note takes the row under Amount. */
const SECOND_SLOT = {
    register: {
        field: `${ROW.fields} ${COL.balance}`,
        line: 'row-start-2 col-start-6 col-span-3 pt-1',
    },
    dialog: {
        field: 'row-start-1 col-start-6 @max-3xl/dialog:row-start-6 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
        line: 'row-start-2 col-start-4 col-span-3 pt-1 @max-3xl/dialog:row-start-7 @max-3xl/dialog:col-span-full',
    },
    dialogNoted: {
        field: 'row-start-1 col-start-6 @max-3xl/dialog:row-start-7 @max-3xl/dialog:col-span-full @max-3xl/dialog:pt-3',
        line: 'row-start-3 col-start-4 col-span-3 pt-1 @max-3xl/dialog:row-start-8 @max-3xl/dialog:col-span-full',
    },
} as const;

/**
 * The rounded-at-import note (ADR-0114 D15, board RV-2), Amount's description, on its own
 * line directly under Amount, with or without a second amount. Wide, on the row under
 * Category and Amount, right-aligned so it ends under the field: the Amount column alone
 * is too narrow for it. Narrow, on the row after Amount, which the dialog keeps free by
 * laying the notes out as DIALOG_SLOT_SECOND, or as DIALOG_SLOT_SECOND_NOTED with a
 * second amount. With a second amount, wide, it spans to the second amount's track as
 * the rate line under it does, so the two lines share Received's right edge
 * (ROUNDED_NOTE_SLOT_SECOND, decided by the owner, 2026-11-17).
 */
const ROUNDED_NOTE_SLOT = 'row-start-2 col-start-4 col-span-2 pt-1 @max-3xl/dialog:row-start-6 @max-3xl/dialog:col-span-full';
const ROUNDED_NOTE_SLOT_SECOND = 'row-start-2 col-start-4 col-span-3 pt-1 @max-3xl/dialog:row-start-6 @max-3xl/dialog:col-span-full';
const ROUNDED_NOTE_LOOK = 'text-right text-[0.6875rem] text-text-muted';

/** The memo grows with its text, so a narrow dialog sets its floor, not its height. */
const NARROW_DIALOG_MEMO = '@max-3xl/dialog:min-h-control-40px @max-3xl/dialog:text-sm';

/**
 * A field pending with the save (aria-disabled) takes the field primitives'
 * disabled look (pending.ts, HOW IT LOOKS); these raw fields have no disabled
 * styles of their own, and dim with the rest of the row.
 */
const PENDING_LOOK = 'aria-disabled:cursor-not-allowed aria-disabled:opacity-50';
/** The memo's grown height, at most: its `max-h-fixed-96px`, about four lines. */
const MEMO_MAX_PX = 96;
/** The footer's Cancel and primary in a narrow dialog: 44px, side by side. */
const NARROW_DIALOG_BUTTON = '@max-3xl/dialog:h-control-44px';

// Date-input helpers (toDateInputValue / todayInputValue /
// shiftDateInputValue) moved to `@/lib/dates` so the date-handling
// rules are centralized — same module that owns `formatLedgerDate`
// and the UTC-vs-local categorization.

/** Surface an ApiError's `.detail` or fall back to a generic
 *  phrase. Used by the parent's mutation error handler too. */
export function patchErrorMessage(error: unknown): string {
    const refusal = saveRefusal(error);
    return typeof refusal === 'string' ? refusal : refusal.detail;
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
    layout = 'register',
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
    submitting,
    hints = true,
    onSecondAmountSideChange,
    amountNote = null,
}: TxnRowEditProps) {
    const pendingProps = usePendingButtonProps();
    const dialog = layout === 'dialog';
    // The header fields' touch size in a narrow dialog (components/ui/fieldSize).
    // Not handed to the splits grid, whose 30px leg rows keep their own sizes.
    const fieldSize = dialog ? NARROW_DIALOG_FIELD : '';
    const headerTagsId = useId();
    const singleTagsId = useId();
    const singleMemoId = useId();
    const memoFromId = useId();
    const tagsFromId = useId();
    const dateId = useId();
    const roundedNoteId = useId();
    const taxDateId = useId();
    const headerMemoId = useId();
    const containerRef = useRef<HTMLDivElement | null>(null);
    const txnMemoRef = useRef<HTMLTextAreaElement | null>(null);

    /** Auto-grow the memo textarea to fit its content (lib/fitTextareaHeight),
     *  capped at ~4 lines (MEMO_MAX_PX) — nearly every memo fits on one
     *  line, so the rare one that exceeds 4 lines scrolls inside the
     *  textarea rather than expanding the form. */
    function adjustMemoHeight(el: HTMLTextAreaElement | null) {
        if (el === null) return;
        fitTextareaHeight(el, MEMO_MAX_PX);
    }

    // All editable state — the seven header/posting fields, the posting
    // mutators and the auto-focus marker. Destructured into the same local
    // names the JSX already used, so moving it out of the shell changed no
    // call site. See hooks/useTxnRowDraft.ts for the three properties
    // (once-only seeding, synchronous addPosting, stable mutator identity)
    // that the consolidation had to preserve.
    const {
        draft: { payee, txnMemo, checkNumber, postedAt, transactedAt, tags, postings },
        setPayee,
        setTxnMemo,
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
    } = useTxnRowDraft(
        mode,
        currency,
        // Read once, when the draft is seeded: the places a stored second amount is
        // seeded at.
        (id) => (id === null ? null : accounts.find((a) => a.id === id)?.currencyCode ?? null),
    );

    // Size the memo field on mount, and whenever the layout swaps between one
    // line and a split: each layout mounts its own memo field, and a collapse
    // fills it without a keystroke, so the joined memo would otherwise sit
    // clipped at one line. Size it again whenever its width changes, which
    // wraps its text, or its placeholder, onto a different number of lines.
    const isSplitLayout = postings.length > 1;
    useEffect(() => {
        const el = txnMemoRef.current;
        if (el === null) return;
        adjustMemoHeight(el);
        return refitOnWidthChange(el, MEMO_MAX_PX);
    }, [isSplitLayout]);

    // What the editor opened with: a save sends only the notes that differ
    // from it (see bank-edit/notesBody.ts). Captured once, like the draft.
    const [notesBaseline] = useState(() => seedNotesBaseline(mode));
    const isCreate = seedIsCreate(mode);
    // The size the transaction's tag limit measures growth from: the stored
    // set, or what a collapse produced from the stored sets. Nothing is stored
    // for a create, so its limit counts from zero, as the server's does.
    const [tagBaseline, setTagBaseline] = useState(() => (isCreate ? 0 : notesBaseline.tags.length));
    // Set once a collapse fills the fields: from then on a save sends them.
    const [collapsed, setCollapsed] = useState(false);

    // Removing the second-to-last line collapses the split: the Memo and Tags
    // fields take the joined memo and the combined tags, so the user sees what
    // will be saved. Cancel discards it with everything else.
    //
    // Stable identity, like the draft's own mutators: it is handed to the
    // memoised split rows. The live values come through a ref.
    const collapseInputs = useRef({ postings, txnMemo, tags });
    useEffect(() => {
        collapseInputs.current = { postings, txnMemo, tags };
    }, [postings, txnMemo, tags]);
    // The collapse takes the whole splits grid away, every kebab and Add split
    // with it, so no leg's control can take the focus Remove split held. The
    // one-posting layout's "Split this transaction →", the posting list's add
    // control there, takes it (ADR-0023 §L), once the layout has swapped and
    // only if focus was lost.
    const splitRef = useRef<HTMLButtonElement | null>(null);
    const collapseFocus = useRef(false);
    useLayoutEffect(() => {
        if (!collapseFocus.current || postings.length !== 1) return;
        collapseFocus.current = false;
        const active = document.activeElement;
        if (active === null || active === document.body) splitRef.current?.focus();
    });
    const removePostingAndCollapse = useCallback((key: string) => {
        const current = collapseInputs.current;
        if (current.postings.length === 2) {
            collapseFocus.current = true;
            const remaining = current.postings.find((p) => p.key !== key);
            if (remaining !== undefined) {
                const collapsed = collapseNotes(
                    { memo: current.txnMemo, tags: current.tags }, remaining);
                setTxnMemo(collapsed.memo);
                setTags(collapsed.tags);
                setCollapsed(true);
                // The limit measures growth from what the server's collapse of
                // the STORED sets would give, not from the edited ones.
                if (!isCreate) {
                    setTagBaseline(unionTags(notesBaseline.tags, remaining.stored?.tags ?? []).length);
                }
            }
        }
        removePosting(key);
    }, [isCreate, notesBaseline, removePosting, setTxnMemo, setTags]);

    // The one-split inheritance hint, as the save will leave it: from the
    // counterparty the draft has, not the one stored, and nothing once the
    // stored posting is replaced (bank-edit/inheritedAfterSave.ts). Shown only
    // on one posting: a split's transaction values are its own.
    const inherited = mode.kind === 'edit' && postings.length === 1
        ? inheritedAfterSave(
            mode.inherited,
            mode.postings.length === 1 ? mode.postings[0]!.legId : null,
            postings[0]!,
        )
        : null;
    const accountName = (id: string | null): string | null =>
        id === null
            ? null
            : accountPaths.get(id) ?? accounts.find((a) => a.id === id)?.name ?? null;
    const inheritedFrom = accountName(inherited?.fromAccountId ?? null);
    // Emptying a field is the way back to the other side's value: the field
    // then inherits again, and says so.
    const memoInheriting = inherited !== null
        && inherited.memo !== null
        && normalizeMemo(txnMemo) === null;
    const tagsInheriting = inherited !== null && inherited.tags.length > 0 && tags.length === 0;

    // Slice 2c.6c: similar-payee recall. Static fetch at row-open
    // (per design — no typeahead refetch). The server returns
    // empty for non-bank-feed rows / no matches / non-single-
    // posting prior rows, so the chip row simply doesn't render
    // when the result is empty. Enabled only for edit-mode
    // single-posting opens; splits don't fit the one-chip = one-
    // (payee, category) shape Tier 1 returns.
    const editSingleHeaderId =
        hints && mode.kind === 'edit' && mode.postings.length === 1
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
    // target, within ±7 days. Clicking a chip arms
    // `mergeFromHeaderId`, and the next save folds THIS row into
    // the candidate, which survives as it was entered. Nothing is
    // copied into the form.
    //
    // Enabled for ANY edit-mode row (not just needs_review) —
    // the matching algorithm decides whether anything fits, and
    // empty results hide the panel. We use mode.headerId here
    // (not editSingleHeaderId) so split-target editors also see
    // the panel; the server still only returns rows whose
    // aggregated source amount matches.
    const editHeaderId = hints && mode.kind === 'edit' ? mode.headerId : null;
    const mergeCandidates = useQuery({
        queryKey: ['merge-candidates', ledgerId, editHeaderId],
        queryFn: () => fetchMergeCandidates(ledgerId, editHeaderId!),
        enabled: editHeaderId !== null,
        staleTime: Infinity,
    });
    // The picked candidate — set when the user clicks a merge
    // candidate chip, sent on the next save, cleared by clicking it
    // again. Null = no merge. The form is left as the user had it.
    const [mergeFromHeaderId, setMergeFromHeaderId] = useState<string | null>(null);

    const sourceAccountId =
        mode.kind === 'new' ? mode.sourceAccountId : mode.sourceAccountId;

    // The currency each posting's Amount is typed and rounded in (ADR-0114 D14, D15).
    // Stable while the accounts are, so the split rows it is handed to keep theirs.
    const currencyOf = useCallback(
        (p: PostingDraft) => postingCurrency(p.counterpartyId, sourceAccountId, accounts, currency),
        [sourceAccountId, accounts, currency],
    );

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

    /**
     * Which side of the posting this editor is standing on.
     *
     * The field holds the COUNTERPARTY, so its name depends on the register:
     * an account's register faces categories, a CATEGORY's register faces
     * money accounts. Labelled "Category" on both, it named the wrong thing on
     * half of them — the same defect the register's own column header had, two
     * lines from the `counterpartyKind` that already made this distinction for
     * the filter picker.
     *
     * Decided ONCE here and handed down as a kind, not a label: the split
     * grid, each posting row and the validation hint all name the counterparty
     * too, and each had its own hard-coded "category" until it was threaded.
     */
    const counterpartyKind: CounterpartyKind = useMemo(
        () => (accounts.some((a) => a.id === sourceAccountId && a.accountType === 'category')
            ? 'account'
            : 'category'),
        [accounts, sourceAccountId],
    );
    const counterparty = counterpartyWords(counterpartyKind);

    /**
     * Which postings carry a second amount, the other account's own figure (ADR-0114
     * D13, D14), decided ONCE here beside `counterpartyKind` and handed down, as that
     * is: the one-posting field, each split line, the validation and the save all read
     * it. A real source and a real counterparty in different currencies; never a
     * category's register, nor a category counterparty (bank-edit/secondAmount.ts).
     * Stable while the accounts are, so the split rows it is handed to keep theirs.
     */
    const sideOf = useCallback(
        (p: PostingDraft): SecondAmountSide | null => secondAmountSide(p, {
            sourceAccountId,
            accounts,
            currency,
            accountName: (id) => accountPaths.get(id) ?? accounts.find((a) => a.id === id)?.name ?? id,
        }),
        [sourceAccountId, accounts, currency, accountPaths],
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

    // Ledger tag dictionary — powers the TagSelect list (existing
    // names + colour) and create-on-first-use. Shared cache
    // key with the register filter + colour provider.
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
        staleTime: 60_000,
    });

    // Live running total (informational; sum-constraint is per-
    // posting at the server, not across the transaction), in the one currency
    // every posting's Amount is in. On a category's register each posting is in
    // its paying account's currency, and when they differ there is no total, as
    // no figure adds currencies (ADR-0114 D7): `amount` is null and the editor
    // shows the mixed marker in its place.
    const sourceTotal = useMemo((): { amount: number | null; currency: string } => {
        const currencies = new Set(postings.map((p) => currencyOf(p)));
        if (currencies.size > 1) return { amount: null, currency };
        let n = 0;
        for (const p of postings) {
            const v = Number(p.amount);
            if (!Number.isNaN(v)) n += v;
        }
        return { amount: n, currency: [...currencies][0] ?? currency };
    }, [postings, currencyOf, currency]);

    // Click-outside cancels the edit (modern web pattern) — unless the host
    // manages its own dismissal (the reminders occurrence dialog, ADR-0049).
    // Not while a save is in flight: Cancel is pending then and refuses, and a
    // press outside is no way round it. Closed mid-save, the editor would take
    // the save's error with it, and the host's answer would land on whatever
    // editor is open by then.
    useEffect(() => {
        if (!cancelOnOutsideClick) return;
        const onPointerDown = (event: PointerEvent) => {
            if (happenedInside(event, containerRef.current)) return;
            if (isSaving) return;
            onCancel();
        };
        document.addEventListener('pointerdown', onPointerDown, true);
        return () => {
            document.removeEventListener('pointerdown', onPointerDown, true);
        };
    }, [onCancel, cancelOnOutsideClick, isSaving]);

    // Validation — Save is disabled until every posting parses to
    // a valid (parseable amount, recognised counterparty) shape.
    // Zero-amount postings ARE allowed: paycheck splits routinely
    // carry zero-amount line items (a tax surcharge, a retirement-plan
    // overflow, a bonus accrual) that flicker positive in some pay periods
    // and stay at zero in others. The DB places no constraint either; the
    // earlier "must be non-zero" client rule was an overreach that
    // locked users out of merging into any paycheck-style target.
    const validation = useMemo(
        () => [
            ...validatePostings(postings, sideOf),
            ...notesIssues({ tags, tagBaseline, postings }),
        ],
        [postings, tags, tagBaseline, sideOf],
    );
    // The same defects as data, for the splits grid: it marks the offending
    // ROW and its summary strip links to the offending FIELD, neither of which
    // a sentence can do. One rule set behind both — see bank-edit/validation.ts.
    const issues = useMemo(
        () => postingIssues(postings, counterpartyKind, sideOf),
        [postings, counterpartyKind, sideOf],
    );
    // When folding into a merge candidate, the editor's postings
    // / payee / memo are about to be discarded — they don't need
    // to validate. Save stays enabled purely on the merge stamp.
    // A save in flight is not a reason to disable it: Save is then
    // pending, keeping focus and refusing a second press (pending.ts).
    const saveDisabled = mergeFromHeaderId === null && validation.length > 0;

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
    const saveButtonLabel = isMerging ? 'Fold into selected →'
        : isNeedsReviewAccept ? 'Accept'
            : (submitLabel ?? 'Save');
    // What Save reads while its own save is in flight. PendingLabel lays both
    // labels out from the first render, so Save never changes size.
    const savingLabel = isMerging ? 'Folding…'
        : isNeedsReviewAccept ? 'Accepting…'
            : (submittingLabel ?? 'Saving…');
    // Enter in the memo presses Save, so the press holds it as a click does
    // (ADR-0023 §L), and Save refuses it while unavailable or busy.
    const saveRef = useRef<HTMLButtonElement | null>(null);

    function buildSaveBody():
        | { kind: 'create'; body: CreateTransactionRequest }
        | { kind: 'patch'; body: PatchTransactionRequest }
        | null
    {
        // Inverted-merge direction: when a candidate is selected, the editor
        // row is about to become a loser — its content is moot. The body is
        // the merge alone, `{ mergeFromHeaderId }`, and the host's save sends
        // it to the merge route, not as a PATCH (bank-edit/save.ts). The
        // candidate stays untouched, so there's nothing to apply to the editor
        // row's payee / memo / postings / tags.
        //
        // FIRST, before the postings are read at all. The validation loop below
        // returns null on a posting with no counterparty, and `saveDisabled`
        // deliberately skips validation while merging — so placed after the
        // loop, folding a row with no counterparty would silently do nothing:
        // the button says "Fold into selected →" and is enabled, and the click
        // reaches a builder that bails before it sees the merge stamp. An
        // uncategorised imported row is exactly that shape.
        // `mode.kind === 'edit'` is the type narrowing this block needs ahead of
        // the create early-return; merging is an edit-only action anyway (the candidates query is gated on an edit
        // header), so it costs nothing to state it.
        if (mergeFromHeaderId !== null && mode.kind === 'edit') {
            // Merge stamp ONLY — no `approve`. Clearing needs_review on the loser
            // is the server's job (TransactionsRepository's merge branch), so
            // sending the flag would be a second mechanism for one invariant and
            // would imply a caller that omits it gets a different outcome. It does
            // not. Identical to the body the investment editor sends.
            return { kind: 'patch', body: { mergeFromHeaderId } };
        }

        // Postings carry shape only; every memo and tag travels in `notes`.
        // An amount the user changed is rounded at its currency's places, as its blur
        // would have (a save by Enter or a click leaves no blur behind it); one left as
        // it was seeded is sent as stored, so a stored figure finer than its currency
        // is kept (ADR-0114 D15). A posting between real accounts in different
        // currencies also sends the other account's own figure, signed opposite to
        // Amount; every other posting leaves the key out, so its body is as before
        // (ADR-0114 D13).
        const items: TransactionPosting[] = [];
        for (const p of postings) {
            const typed = Number(p.amount);
            if (p.counterpartyId === null || Number.isNaN(typed)) return null;
            const other = secondAmountToSend(p, sideOf(p));
            items.push({
                legId: p.legId,
                counterpartyAccountId: p.counterpartyId,
                amount: p.amount === p.seededAmount ? typed : roundToMinorUnits(typed, currencyOf(p)),
                ...(other !== null ? { counterpartyAmount: other } : {}),
            });
        }

        // Only what changed (see bank-edit/notesBody.ts). The side is the
        // register's account, which becomes the owner when this save splits a
        // one-split.
        const notes = buildNotes({
            side: mode.sourceAccountId,
            isNew: isCreate,
            memo: txnMemo,
            tags,
            postings,
            baseline: notesBaseline,
            collapsed,
            linesRebuilt: mode.kind === 'new' && mode.prefillIsStored === true,
        });

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
                checkNumber:
                    checkNumber.trim().length === 0 ? null : checkNumber.trim(),
                sourceAccountId: mode.sourceAccountId,
                postings: items,
                notes,
            };
            return { kind: 'create', body };
        }

        const body: PatchTransactionRequest = {
            // Every header field, every save. Since mig 230 an explicit null
            // CLEARS the column, so an emptied payee / check box now actually
            // empties it — under the old override layer the server read the
            // null as "leave alone" and handed the old text back. Memo and tags
            // are the exception: `notes` carries only what changed.
            payee: payee.trim().length === 0 ? null : payee.trim(),
            checkNumber:
                checkNumber.trim().length === 0 ? null : checkNumber.trim(),
            postedAt: `${postedAt}T00:00:00.000Z`,
            // See the create path: blank is the posted date, never null.
            transactedAt: `${transactedAt.length === 0 ? postedAt : transactedAt}T00:00:00.000Z`,
            postings: {
                sourceAccountId: mode.sourceAccountId,
                items,
            },
            notes,
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
        if (isSaving || saveDisabled) return;
        const built = buildSaveBody();
        if (built === null) return;
        if (built.kind === 'create') onSaveCreate?.(built.body);
        else onSavePatch?.(built.body);
    }

    function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
        // `defaultPrevented` is the whole guard, and it is load-bearing.
        // Without it, Escape inside an OPEN list would cancel the entire
        // transaction: dismiss a dropdown you opened by mistake, lose every
        // leg of a long split. Every list in this editor (category, payee,
        // tags) marks the Escape that closes it with preventDefault() and
        // stops it, so the first Escape closes the list and the next one, with
        // no list open, cancels here (ADR-0023 §F). The check holds even for a
        // control that marks without stopping.
        //
        // While a save is in flight the Escape is marked and refused, as the
        // pending Cancel it is the shortcut for refuses a press. The pending
        // Save keeps focus in the editor, so the key does reach here then. The
        // mark keeps a dialog this editor sits in open too.
        if (event.key === 'Escape' && !event.defaultPrevented) {
            event.preventDefault();
            if (isSaving) return;
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
    // The one posting's second amount, while it has one (ADR-0114 D14). A split's
    // lines carry theirs in the splits grid.
    const onlySide = postings.length === 1 ? sideOf(onlyPosting) : null;
    // Told to the host by value, so a new side object for the same side tells nothing.
    const sideAccountId = onlySide?.accountId ?? null;
    const sideAccountName = onlySide?.accountName ?? '';
    const sideCurrency = onlySide?.currency ?? '';
    const sideDirection = onlySide?.direction ?? 'received';
    useEffect(() => {
        onSecondAmountSideChange?.(sideAccountId === null ? null : {
            accountId: sideAccountId,
            accountName: sideAccountName,
            currency: sideCurrency,
            direction: sideDirection,
        });
    }, [onSecondAmountSideChange, sideAccountId, sideAccountName, sideCurrency, sideDirection]);
    const patchOnly = useCallback(
        (fields: Partial<PostingDraft>) => patchPosting(onlyPosting.key, fields),
        [patchPosting, onlyPosting.key],
    );

    // The rounded note, where it is drawn: the dialog's one-posting form (ADR-0114 D15).
    const roundedNote = dialog && postings.length === 1 && amountNote ? amountNote : null;
    // A narrow dialog gives the note the row after Amount: the second amount's layout
    // already leaves its rows free. With both, the note keeps that row and the second
    // amount and its rate line move down one (ADR-0114 D15).
    const slot = dialog
        ? (onlySide !== null && roundedNote !== null
            ? DIALOG_SLOT_SECOND_NOTED
            : onlySide !== null || roundedNote !== null ? DIALOG_SLOT_SECOND : DIALOG_SLOT)
        : REGISTER_SLOT;
    const secondSlot = dialog
        ? (roundedNote !== null ? SECOND_SLOT.dialogNoted : SECOND_SLOT.dialog)
        : SECOND_SLOT.register;
    // The header grid's own classes and template. The register's rides the
    // register's columns (`cols`); the dialog's is DIALOG_GRID, with no inset of
    // its own because the dialog's body already has one, and one track more while the
    // one posting carries a second amount.
    const headerGrid = dialog
        ? {
              className: cn(
                  'grid items-start gap-x-2 py-2',
                  onlySide !== null ? DIALOG_GRID_SECOND : DIALOG_GRID,
                  onlySide !== null && roundedNote !== null && DIALOG_ROWS_SECOND_NOTED,
              ),
              style: undefined,
          }
        : { className: 'grid items-start gap-x-2 px-3 py-2', style: { gridTemplateColumns: cols } };

    // Declared once and rendered in BOTH layouts. Inside the single-posting arm
    // only, a split row's candidates query would fire, the server answer, the data
    // land in cache — and nothing render it. Merge would be unreachable from any
    // split bank row, which is exactly where an imported duplicate of a paycheck
    // would need folding in. The investment editor mounts
    // its panel once for the same reason: it has only one layout to fall out of.
    const mergePanel = (
        <MergeCandidatesPanel
            candidates={mergeCandidates.data ?? []}
            accountPaths={accountPaths}
            selectedHeaderId={mergeFromHeaderId}
            pending={isSaving}
            dialog={dialog}
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

    // The footer: the leading slot and the Split link on the left, Cancel and
    // the primary on the right. In a narrow dialog it is two rows: the left
    // group spread across the width, then Cancel and the primary as 44px
    // buttons sharing it equally.
    const footerClass = dialog
        ? {
              bar: 'mt-1 flex items-center justify-between gap-2 border-t border-border/30 pt-3 @max-3xl/dialog:flex-col @max-3xl/dialog:items-stretch @max-3xl/dialog:gap-2.5',
              leading: 'flex items-center gap-3 @max-3xl/dialog:justify-between @max-3xl/dialog:empty:hidden',
              buttons: 'flex gap-2 @max-3xl/dialog:grid @max-3xl/dialog:grid-cols-2',
          }
        : {
              bar: 'flex items-center justify-between gap-2 border-t border-border/30 px-3 py-2',
              leading: 'flex items-center gap-3',
              buttons: 'flex gap-2',
          };
    const actionButtons = (
        <div className={footerClass.buttons}>
            <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={onCancel}
                pending={isSaving}
                title="Cancel (Esc)"
                className={dialog ? NARROW_DIALOG_BUTTON : undefined}
            >
                Cancel
            </Button>
            <Button
                ref={saveRef}
                type="button"
                variant="primary"
                size="sm"
                onClick={handleSave}
                disabled={saveDisabled}
                pending={isSaving}
                title={
                    validation.length > 0
                        ? validation.join('\n')
                        : saveButtonLabel
                }
                className={dialog ? NARROW_DIALOG_BUTTON : undefined}
            >
                <PendingLabel pending={submitting ?? isSaving} label={saveButtonLabel} pendingLabel={savingLabel} />
            </Button>
        </div>
    );

    return (
        <div
            ref={containerRef}
            // A register row in the register; in a dialog there is no grid to
            // be a row of, so it is a named group, without the editing-row band.
            role={dialog ? 'group' : 'row'}
            aria-label={dialog ? 'Transaction' : undefined}
            data-editing="true"
            data-creating={mode.kind === 'new' || undefined}
            style={{ width: '100%' }}
            className={dialog ? undefined : 'border-y border-accent/40 bg-accent-soft/10'}
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
                <FieldSizeContext.Provider value={fieldSize}>
                <div className={headerGrid.className} style={headerGrid.style}>
                    {/* Row 1 — the fields. */}
                    <div className={cn(slot.date, 'min-w-0')}>
                        <DateField
                            label="Date"
                            id={dateId}
                            value={postedAt}
                            onChange={setPostedAt}
                            pending={isSaving}
                            autoFocus
                        />
                    </div>
                    <label className={cn(slot.check, 'flex min-w-0 flex-col gap-1')}>
                        <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">Check #</span>
                        <input
                            type="text"
                            value={checkNumber}
                            placeholder=""
                            aria-label="Check number"
                            onChange={(e) => setCheckNumber(e.target.value)}
                            className={cn(
                                'h-control-28px w-full rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                                PENDING_LOOK,
                                fieldSize,
                            )}
                            {...pendingTextProps<HTMLInputElement>(isSaving)}
                        />
                    </label>
                    <label className={cn(slot.payee, 'flex min-w-0 flex-col gap-1')}>
                        <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">Payee</span>
                        <Typeahead<PayeeSuggestion>
                            items={payees}
                            value={payee}
                            onChange={setPayee}
                            getKey={(p) => p.name}
                            getLabel={(p) => p.name}
                            pending={isSaving}
                            aria-label="Payee"
                        />
                    </label>
                    <div className={cn(slot.category, 'min-w-0')}>
                        <AccountCategoryPicker
                            accounts={accounts}
                            isEligible={isEligibleCounterparty}
                            frequent={frequent}
                            valueId={onlyPosting.counterpartyId}
                            onChangeId={(id) =>
                                patchPosting(onlyPosting.key, { counterpartyId: id })}
                            label={counterparty.noun}
                            placeholder="Category or account…"
                            ariaLabel={counterparty.noun}
                            pending={isSaving}
                        />
                    </div>
                    <label className={cn(slot.amount, 'flex min-w-0 flex-col gap-1')}>
                        <span className="text-right text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">Amount</span>
                        <MoneyInput
                            currency={currencyOf(onlyPosting)}
                            value={onlyPosting.amount}
                            pending={isSaving}
                            aria-label="Amount"
                            aria-describedby={roundedNote !== null ? roundedNoteId : undefined}
                            onValueChange={(amount) => patchPosting(onlyPosting.key, { amount })}
                            className={cn(
                                'h-control-28px w-full rounded border border-border bg-surface px-2 text-right font-mono text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                                // Same bank-register pairing as the split rows
                                // — a debit is red, a credit is plain. This
                                // field carried no colour at all, so a single
                                // transaction changed colour merely by being
                                // opened for edit.
                                Number(onlyPosting.amount) < 0
                                    && onlyPosting.amount.trim().length > 0
                                    && !Number.isNaN(Number(onlyPosting.amount))
                                    ? 'text-state-danger'
                                    : 'text-text',
                                PENDING_LOOK,
                                fieldSize,
                            )}
                        />
                    </label>
                    {/* Amount's rounded-at-import note, on its own line under Amount and
                        next after it in reading order, with or without a second amount;
                        with one, ending where the rate line under it ends (ADR-0114 D15). */}
                    {roundedNote !== null ? (
                        <p
                            id={roundedNoteId}
                            className={cn(onlySide !== null ? ROUNDED_NOTE_SLOT_SECOND : ROUNDED_NOTE_SLOT, ROUNDED_NOTE_LOOK)}
                        >
                            {roundedNote}
                        </p>
                    ) : null}
                    {/* The second amount, right of Amount, while the counterparty is a
                        real account in another currency (ADR-0114 D14). In the register
                        it takes the Balance track, so the pre-edit balance is not shown
                        while it does; the balance is a register column, and a dialog has
                        none. */}
                    {onlySide !== null ? (
                        <OneLineSecondAmount
                            ledgerId={ledgerId}
                            posting={onlyPosting}
                            side={onlySide}
                            currency={currencyOf(onlyPosting)}
                            date={postedAt}
                            pending={isSaving}
                            onPatch={patchOnly}
                            fieldClassName={secondSlot.field}
                            lineClassName={secondSlot.line}
                            fieldSize={fieldSize}
                            pendingLook={PENDING_LOOK}
                        />
                    ) : mode.kind === 'edit' && !dialog ? (
                        <span
                            title="Pre-edit running balance"
                            className={cn(ROW.fields, COL.balance, 'pt-5 text-right font-mono text-xs tabular-nums text-text-subtle opacity-60')}
                        >
                            {rowBalance(mode)}
                        </span>
                    ) : null}

                    {/* Row 2 — the hints under Payee; rows 2 and 3 in a dialog
                        with a second amount and the rounded note, where the rate
                        line is on row 3 (DIALOG_SLOT_SECOND_NOTED). */}
                    <div className={cn(slot.hints, 'flex min-w-0 flex-col gap-1 empty:hidden')}>
                        <SimilarPayeesPanel
                            suggestions={similarPayees.data ?? []}
                            accountPaths={accountPaths}
                            pending={isSaving}
                            dialog={dialog}
                            onApply={(s) => {
                                setPayee(s.payee);
                                patchPosting(onlyPosting.key, {
                                    counterpartyId: s.counterpartyAccountId,
                                });
                            }}
                        />
                        {mergePanel}
                    </div>

                    {/* Rows 3 and 4 — Tax date, Memo and Tags: captions, then
                        controls; rows 4 and 5 in a dialog with a second amount
                        and the rounded note (DIALOG_SLOT_SECOND_NOTED). */}
                    <FieldLabel htmlFor={taxDateId} className={slot.taxCaption}>
                        Tax date
                    </FieldLabel>
                    <div className={cn(slot.taxControl, 'flex min-w-0')}>
                        <DateField
                            bare
                            label="Tax date"
                            id={taxDateId}
                            value={transactedAt}
                            onChange={setTransactedAt}
                            pending={isSaving}
                            hint="Blank = same as posted"
                        />
                    </div>
                    {/* This side's memo. While it inherits, the field is empty
                        and shows the other side's as placeholder, behind the
                        same marker the register draws. */}
                    <div className={cn(slot.memoCaption, 'min-w-0')}>
                        <NotesFieldCaption
                            htmlFor={singleMemoId}
                            fromId={memoFromId}
                            label="Memo"
                            otherSide={inheritedFrom}
                            inheriting={memoInheriting}
                        />
                    </div>
                    <div className={cn(slot.memoControl, 'min-w-0')}>
                        <span className="relative block">
                            {memoInheriting ? (
                                <InheritedMarker
                                    from={inheritedFrom}
                                    className="pointer-events-none absolute left-2 top-[0.5625rem]"
                                />
                            ) : null}
                            <textarea
                                id={singleMemoId}
                                ref={txnMemoRef}
                                rows={1}
                                value={txnMemo}
                                onChange={(e) => {
                                    setTxnMemo(e.target.value);
                                    adjustMemoHeight(e.target);
                                }}
                                onKeyDown={(e) => {
                                    if (e.key === 'Enter' && !e.shiftKey) {
                                        e.preventDefault();
                                        e.stopPropagation();
                                        saveRef.current?.click();
                                    }
                                }}
                                aria-describedby={memoInheriting ? memoFromId : undefined}
                                placeholder={memoInheriting
                                    ? inherited!.memo!
                                    : 'Optional (Shift+Enter for new line)'}
                                className={cn(
                                    'block min-h-control-28px max-h-fixed-96px w-full resize-none overflow-y-auto rounded border border-border bg-surface py-1 pr-2 text-xs placeholder:text-text-subtle focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                                    PENDING_LOOK,
                                    memoInheriting ? 'pl-6' : 'pl-2',
                                    dialog && NARROW_DIALOG_MEMO,
                                )}
                                {...pendingTextProps<HTMLTextAreaElement>(isSaving)}
                            />
                        </span>
                    </div>
                    <div className={cn(slot.tagsCaption, 'min-w-0')}>
                        <NotesFieldCaption
                            htmlFor={singleTagsId}
                            fromId={tagsFromId}
                            label="Tags"
                            otherSide={inheritedFrom}
                            inheriting={tagsInheriting}
                        />
                    </div>
                    <div className={cn(slot.tagsControl, 'flex min-w-0')}>
                        <TagSelect
                            id={singleTagsId}
                            tags={tags}
                            placeholderTags={inherited?.tags}
                            placeholderFrom={inheritedFrom}
                            maxCount={tagCountCeiling(tagBaseline)}
                            allTags={tagsQuery.data ?? []}
                            onChange={setTags}
                            pending={isSaving}
                            aria-label="Tags"
                            aria-describedby={tagsInheriting ? tagsFromId : undefined}
                        />
                    </div>
                </div>
                </FieldSizeContext.Provider>
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
                <div className={footerClass.bar}>
                    <div className={footerClass.leading}>
                        {footerLeading}
                        {/* Split affordance — left-aligned secondary action that
                            doesn't compete with the primary Save. Adds an empty
                            posting; the form re-renders into the multi-split
                            branch with focus on the new posting's amount. */}
                        <button
                            ref={splitRef}
                            type="button"
                            {...pendingProps(isSaving, () => addPosting())}
                            title={`Add a second posting to split this transaction across ${counterparty.plural}`}
                            className={cn(
                                'text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                                dialog && '@max-3xl/dialog:min-h-control-32px @max-3xl/dialog:text-sm',
                            )}
                        >
                            Split this transaction →
                        </button>
                    </div>
                    {actionButtons}
                </div>
            </>
        ) : (
        <>
            {/* Root row — the register's 8-column grid and the one-split
                editor's rows (ROW above), so Date / Check # / Payee / Amount
                / Balance line up with the register's columns and the notes
                sit where the one-split editor puts them. The Category column
                has nothing in the first row (each line below carries its
                own) but keeps its track, so the Total stays over Amount. */}
            <FieldSizeContext.Provider value={fieldSize}>
            <div
                className={cn(headerGrid.className, 'border-b border-border/40')}
                style={headerGrid.style}
            >
                {/* Row 1 — the fields. */}
                <div className={cn(slot.date, 'min-w-0')}>
                    <DateField
                        label="Date"
                        id={dateId}
                        value={postedAt}
                        onChange={setPostedAt}
                        pending={isSaving}
                        autoFocus
                    />
                </div>
                <label className={cn(slot.check, 'flex min-w-0 flex-col gap-1')}>
                    <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">Check #</span>
                    <input
                        type="text"
                        value={checkNumber}
                        placeholder=""
                        aria-label="Check number"
                        onChange={(e) => setCheckNumber(e.target.value)}
                        className={cn(
                            'h-control-28px w-full rounded border border-border bg-surface px-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                            PENDING_LOOK,
                            fieldSize,
                        )}
                        {...pendingTextProps<HTMLInputElement>(isSaving)}
                    />
                </label>
                <label className={cn(slot.payee, 'flex min-w-0 flex-col gap-1')}>
                    <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">Payee</span>
                    <Typeahead<PayeeSuggestion>
                        items={payees}
                        value={payee}
                        onChange={setPayee}
                        getKey={(p) => p.name}
                        getLabel={(p) => p.name}
                        pending={isSaving}
                        aria-label="Payee"
                    />
                </label>
                {/* Amount column — read-only sum of the posting amounts. No
                    "Total:" label needed (the register's AMOUNT header above
                    identifies it). The sum's hover only over a sum: "mixed"
                    says its own. */}
                <span
                    title={sourceTotal.amount === null ? undefined : 'Sum of postings (saved as the transaction total)'}
                    className={cn(slot.amount, 'pt-5 text-right font-mono text-xs tabular-nums text-text')}
                >
                    {sourceTotal.amount === null
                        ? <Mixed />
                        : formatCurrency(sourceTotal.amount, sourceTotal.currency)}
                </span>
                {/* Balance column — pre-edit reference at reduced opacity. A
                    register column; a dialog has none. */}
                {mode.kind === 'edit' && !dialog ? (
                    <span
                        title="Pre-edit running balance"
                        className={cn(ROW.fields, COL.balance, 'pt-5 text-right font-mono text-xs tabular-nums text-text-subtle opacity-60')}
                    >
                        {rowBalance(mode)}
                    </span>
                ) : null}

                {/* Row 2 — the hints under Payee. */}
                <div className={cn(slot.hints, 'flex min-w-0 flex-col gap-1 empty:hidden')}>
                    {mergePanel}
                </div>

                {/* Rows 3 and 4 — Tax date, then the transaction's own memo
                    and tags, distinct from each line's; the captions say so,
                    so the two levels read as different things. ADR-0009 put
                    tags at header level only; ADR-0103 gave postings their
                    own, so this Tags control is the HEADER's — the claim about
                    the transaction, not about any one posting. Same control on
                    both, so the concept does not look like two.

                    The Memo caption is wider than the Payee column at the
                    column's floor. It wraps there; the captions row is
                    bottom-aligned, so the controls stay in one line. */}
                <FieldLabel htmlFor={taxDateId} className={slot.taxCaption}>
                    Tax date
                </FieldLabel>
                <div className={cn(slot.taxControl, 'flex min-w-0')}>
                    <DateField
                        bare
                        label="Tax date"
                        id={taxDateId}
                        value={transactedAt}
                        onChange={setTransactedAt}
                        pending={isSaving}
                        hint="Blank = same as posted"
                    />
                </div>
                <FieldLabel htmlFor={headerMemoId} className={cn(slot.memoCaption, 'min-w-0')}>
                    Memo (whole transaction)
                </FieldLabel>
                <div className={cn(slot.memoControl, 'min-w-0')}>
                    <textarea
                        id={headerMemoId}
                        ref={txnMemoRef}
                        rows={1}
                        value={txnMemo}
                        onChange={(e) => {
                            setTxnMemo(e.target.value);
                            adjustMemoHeight(e.target);
                        }}
                        onKeyDown={(e) => {
                            if (e.key === 'Enter' && !e.shiftKey) {
                                e.preventDefault();
                                e.stopPropagation();
                                saveRef.current?.click();
                            }
                        }}
                        placeholder={
                            postings.length > 1
                                ? 'Optional note that applies to the whole split (Shift+Enter for new line)'
                                : 'Optional (Shift+Enter for new line)'
                        }
                        className={cn(
                            'block min-h-control-28px max-h-fixed-96px w-full resize-none overflow-y-auto rounded border border-border bg-surface px-2 py-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                            PENDING_LOOK,
                            dialog && NARROW_DIALOG_MEMO,
                        )}
                        {...pendingTextProps<HTMLTextAreaElement>(isSaving)}
                    />
                </div>
                <FieldLabel htmlFor={headerTagsId} className={cn(slot.tagsCaption, 'min-w-0')}>
                    Tags (whole transaction)
                </FieldLabel>
                <div className={cn(slot.tagsControl, 'flex min-w-0')}>
                    <TagSelect
                        id={headerTagsId}
                        tags={tags}
                        maxCount={tagCountCeiling(tagBaseline)}
                        allTags={tagsQuery.data ?? []}
                        onChange={setTags}
                        pending={isSaving}
                        aria-label="Tags (whole transaction)"
                    />
                </div>
            </div>
            </FieldSizeContext.Provider>

            {/* The legs keep the splits grid's own columns in every layout. In a
                dialog too narrow for them, its scrollport scrolls sideways, so
                the dialog around it does not. */}
            <SplitsGrid
                allTags={tagsQuery.data ?? []}
                postings={postings}
                issues={issues}
                accounts={accounts}
                isEligibleCounterparty={isEligibleCounterparty}
                counterpartyKind={counterpartyKind}
                frequent={frequent}
                currency={sourceTotal.currency}
                currencyOf={currencyOf}
                sideOf={sideOf}
                ledgerId={ledgerId}
                date={postedAt}
                total={sourceTotal.amount}
                pending={isSaving}
                focusKey={focusKey}
                onAutoFocused={() => setFocusKey(null)}
                onPatch={patchPosting}
                onAdd={() => addPosting()}
                onRemove={removePostingAndCollapse}
                onReorder={reorderPostings}
                onMove={movePosting}
            />

            {/* Bottom action row — optional leading slot left, Cancel + Save right. */}
            <div className={footerClass.bar}>
                <div className={footerClass.leading}>{footerLeading}</div>
                {actionButtons}
            </div>
        </>
        )}

        {saveError ? (
            <p
                role="alert"
                className="mx-3 mb-2 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-[0.6875rem] text-state-danger"
            >
                {saveErrorWords(saveError, postings.map(sideOf))}
            </p>
        ) : null}
        </div>
    );
}
