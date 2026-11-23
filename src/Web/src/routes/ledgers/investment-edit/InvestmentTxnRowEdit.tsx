import { Fragment, useId, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import {
    createInvestmentTransaction,
    fetchFrequentCounterparties,
    fetchHoldings,
    fetchInvestmentMergeCandidates,
    fetchSecurities,
    fetchPayees,
    fetchSimilarPayees,
    fetchTags,
    mergeInvestmentTransaction,
    patchInvestmentTransaction,
} from '@/lib/api';
import { AddSecurityDialog } from '../components/AddSecurityDialog';
import { Button } from '@/components/ui/Button';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { FieldSizeContext, NARROW_DIALOG_FIELD } from '@/components/ui/fieldSize';
import { pendingChoiceProps, pendingTextProps, usePendingButtonProps } from '@/components/ui/pending';
import { happenedInside } from '@/components/ui/popoverDismissal';
import { Skeleton } from '@/components/ui/Skeleton';
import { cn } from '@/lib/cn';
import { invalidateHomeReads } from '@/lib/registerInvalidation';
import { buildAccountPathMap, displayAccountPath } from '@/lib/accountPath';
import { Typeahead } from '@/components/ui/Typeahead';
import { TagSelect } from '@/components/tags/TagSelect';
import { SimilarPayeesPanel } from '../bank-edit/fields/SimilarPayeesPanel';
import { mergeChipTitle } from '../bank-edit/fields/mergeChipTitle';
import { DIALOG_NARROW_HINT_CHIP, DIALOG_NARROW_HINT_STRIP } from '../bank-edit/fields/hintChips';
import { NotesFieldCaption } from '../bank-edit/fields/NotesFieldCaption';
import { InheritedMarker } from '@/components/register/InheritedMarker';
import {
    normalizeMemo,
    tagCountAccepted,
    tagCountCeiling,
    TAG_LIMIT,
} from '@/lib/annotations/notes';
import { DateField } from '../components/DateField';
import { formatCurrency, formatShares, formatSignedAmount, roundToMinorUnits } from '@/lib/money';
import { formatInvestmentAction } from '../register/strategies/investmentStrategy';
import type {
    AccountSummary,
    CreateInvestmentTransactionRequest,
    InvestmentMergeCandidate,
    LedgerInvestmentAction,
    PatchInvestmentTransactionRequest,
    PayeeSuggestion,
    RegisterEntry,
} from '@/lib/types';

import {
    ACTION_LAYOUTS, ACTION_PICKER_ENTRIES, isLinkedAction, transferAmountSide, type FieldKey,
} from './actionLayout';
import { secondAmountRequired, signedSecondAmount, type SecondAmountSide } from '@/lib/secondAmount';
import { investmentSaveErrorWords } from '../saveErrorWords';
import { amountForAction } from './hintToDraft';
import { useInvestmentTxnDraft } from './hooks/useInvestmentTxnDraft';
import { inheritedAfterSave, type InvestmentInheritedNotes } from './inheritedAfterSave';
import {
    draftToCreateRequest,
    draftToPatchRequest,
    draftToStoredRequest,
    type InvestmentTxnDraft,
} from './validation';

import { AmountField } from './fields/AmountField';
import { CategoryField } from './fields/CategoryField';
import { FeeField } from './fields/FeeField';
import { FifoPreviewPopover } from './fields/FifoPreviewPopover';
import { PriceField } from './fields/PriceField';
import { SecurityField } from './fields/SecurityField';
import { SharesField } from './fields/SharesField';
import { TransferField } from './fields/TransferField';
import { TransferAmountField } from './fields/TransferAmountField';

/** A dialog inside an editor (the editors mark their container data-editing). */
const EDITOR_DIALOG = '[data-editing] [role="dialog"]';

/**
 * Investment register editor (ADR-0029). The editor speaks the
 * action × field matrix natively rather than the bank-shape
 * postings list. Two modes via discriminated `mode` prop:
 *
 *   * <c>'new'</c> — blank draft; Save POSTs to
 *     <c>/investment-transactions</c>.
 *   * <c>'edit'</c> — seeded from an existing header's legs (via
 *     <c>legsToDraft</c>); Save PATCHes the same header (ADR-0025
 *     wholesale replace).
 *
 * Layout, validation, and per-field components are identical
 * across modes — only the save path differs.
 */
export type InvestmentTxnRowEditMode =
    | {
          kind: 'new';
          /** Optional seed for the Duplicate flow: a draft copied from
           *  an existing row (built via <c>legsToDraft</c>), used to
           *  PRE-FILL the form. The save path is unchanged — a 'new'
           *  editor always POSTs a brand-new header (no headerId), so
           *  this only populates the fields. Omitted for a plain
           *  "+ New" open, which starts blank. Stable reference
           *  required (caller should useState / useMemo). */
          initialDraft?: InvestmentTxnDraft;
          /** Fires with the newly-created header id after a successful
           *  save. Parent typically navigates ?focus=<id> so the
           *  register re-seeds with the new row anchored. */
          onCreated: (headerId: string) => void;
      }
    | {
          kind: 'edit';
          /** Header being patched. */
          headerId: string;
          /** Seed draft built from the existing legs via
           *  <c>legsToDraft</c>. Stable reference required (caller
           *  should useMemo). */
          initialDraft: InvestmentTxnDraft;
          /** ADR-0031 Phase 3d.2: provider-mapping hint for the
           *  upgrade flow. Set when the row originated from a sync
           *  provider AND the classifier extracted a ticker from
           *  the description; the editor passes this back on PATCH
           *  so the server can record (providerKey, providerSecurityId)
           *  → security_id in <c>provider_security_mappings</c>.
           *  Null on rows without a recognizable ticker; ignored
           *  by the server when the resolved security_id is null. */
          providerSecurityHint?: { providerKey: string; providerSecurityId: string };
          /** TRUE when the row carries <c>needs_review</c> (feed-
           *  imported, not yet user-approved). The save button
           *  surfaces "Accept" instead of "Save" so the label
           *  matches what the action does: PATCH-to-investment-
           *  shape clears the flag alongside the row upgrade.
           *  Mirrors the bank editor's pattern. */
          needsReview?: boolean;
          /** What this brokerage shows while its own memo / tags are blank,
           *  as the row is stored, and where that comes from: the fields'
           *  placeholder and their "from X" caption while the draft keeps the
           *  leg it is on (inheritedAfterSave.ts). Null or omitted when
           *  nothing is inherited. */
          inherited?: InvestmentInheritedNotes | null;
          /** Fires after a successful PATCH. The parent swaps the
           *  saved header's rows in place for <c>entries</c> with
           *  <c>register.replaceHeaderEntries</c> (preserves scroll
           *  position + chronological order).
           *
           * @param entries every freshly-resolved entry the saved header has
           *   in the brokerage's register, in register order; empty when it
           *   no longer belongs in this register.
           * @param mergedIntoHeaderId set ONLY on a merge: the SURVIVOR's
           *   header id, where `entries` are the survivor's and the row
           *   being edited has just become the loser. The page needs both —
           *   the loser to remove and the survivor to place — and the entries
           *   alone cannot say which row to drop. Without this the page
           *   swapped the survivor's data onto the loser's row and left two
           *   entries sharing one header id.
           */
          onSaved: (
              entries: RegisterEntry[],
              mergedIntoHeaderId?: string | null,
          ) => void;
      }
    | {
          // Adjust-at-post (ADR-0049): reuse this editor to commit an EDITED
          // reminder occurrence. No internal mutation — the modal host owns the
          // fire request.
          kind: 'fire';
          /** Seed built via legsToDraft against a reminder template (occurrence
           *  post, or reminder EDIT). OMITTED for a from-scratch reminder CREATE
           *  — the draft then starts blank, like 'new'. Stable reference required
           *  (useMemo at the call site) when supplied. */
          initialDraft?: InvestmentTxnDraft;
          /** `initialDraft` is a stored template being edited (a reminder
           *  EDIT), not a copy: memo and tags are then sent only when they
           *  differ from it, and the tag limit measures growth from it.
           *  Omitted / false: a create, held to the limit from none. */
          initialIsStored?: boolean;
          /** Commit handler — receives the create-shaped request; the host wraps
           *  it (FireInvestmentReminderRequest for /fire/investment, or
           *  Create/EditInvestmentReminderRequest for the reminder editor) and
           *  owns the POST/PATCH. No internal mutation. */
          onSubmit: (req: CreateInvestmentTransactionRequest) => void;
      };

/**
 * Where the editor is hosted. `register` (the default) is a register row on the
 * register's template (`cols`). `dialog` is inside an EditorDialog: the same
 * form rows, filling the dialog, until the dialog is narrower than
 * `@3xl/dialog`; then every field takes its own row at touch size, and the
 * footer's Cancel and primary become 44px buttons side by side, as the bank
 * editor's dialog layout does.
 */
export type InvestmentTxnRowEditLayoutProps =
    | {
          layout?: 'register';
          /** Register grid template — the editor uses the same grid so
           * its rows align with the column-header strip above. */
          cols: string;
      }
    | { layout: 'dialog'; cols?: never };

export type InvestmentTxnRowEditProps = InvestmentTxnRowEditCommonProps & InvestmentTxnRowEditLayoutProps;

interface InvestmentTxnRowEditCommonProps {
    ledgerId: string;
    /** The user-visible brokerage account this txn belongs to. */
    brokerageAccountId: string;
    /** Every account in the ledger; field components filter
     * down per their needs (category-kind / bank-shape / etc.). */
    accounts: readonly AccountSummary[];
    /** The brokerage's currency, when the host's data carries it (a reminder
     *  occurrence's, a home entry's): a list without the brokerage, a deactivated
     *  one left out, cannot say it. Otherwise it is read from `accounts`, and
     *  until either knows it the editor prints no figure. */
    currency?: string;
    /** Whether `is_trade_commission` is on for this brokerage
     * (drives the fee field's contextual hint). */
    isTradeCommission: boolean;
    /** Optional left-aligned footer content (e.g. the reminders dialog's Skip
     *  action), rendered alongside Cancel / Save (ADR-0049). */
    footerLeading?: ReactNode;
    /** Override the primary button's label + busy label. The reminders
     *  occurrence dialog posts the occurrence, so it passes "Post" / "Posting…";
     *  defaults to "Save" / "Saving…" (the needs-review "Accept" label still
     *  takes precedence in edit mode). */
    submitLabel?: string;
    submittingLabel?: string;
    /** Whether the host's request in flight is this editor's own submit, so
     *  Save reads its busy label. A host whose other request also holds the
     *  editor (the reminders dialog's Skip) passes its submit's own pending
     *  state here, and Save reads its resting label while the other request is
     *  in flight. Defaults to `isSaving`; the editor's own save always counts. */
    submitting?: boolean;
    /**
     * Host-owned in-flight flag, for `mode.kind === 'fire'`.
     *
     * In that mode the HOST owns the request — the editor's own mutations never
     * run — so `mutation.isPending` is permanently false and every control stayed
     * live for the whole POST. `submittingLabel` was being passed and could never
     * appear, Cancel stayed clickable, and a second click on Post issued a second
     * POST: a duplicate transaction. The bank editor has always taken this prop.
     */
    isSaving?: boolean;
    onCancel: () => void;
    mode: InvestmentTxnRowEditMode;
    /** Offer the Similar and Merge candidates strips (edit mode). Defaults to
     *  true. The home screen's Review turns them off for a row that is no
     *  longer awaiting review, which saves as a plain edit. */
    hints?: boolean;
    /** The Review editor's rounded-at-import note, "Rounded from <bank figure> as sent by
     *  the bank" (ADR-0114 D15): Amount's description, muted under it. Only the home
     *  screen's Review passes it. */
    amountNote?: string | null;
}

/**
 * The new-investment-txn editor. Lifecycle (NEW mode only this PR):
 * 1. User picks an action from the dropdown.
 * 2. Editor reveals the per-action fields per ACTION_LAYOUTS.
 * 3. User fills them in; the validation hook gates Save.
 * 4. Save posts to /investment-transactions; on 201, calls
 *    onCreated(headerId).
 */
export function InvestmentTxnRowEdit({
    ledgerId,
    brokerageAccountId,
    accounts,
    currency: hostCurrency,
    isTradeCommission,
    // `layout` below is the action's field layout; this is the host's.
    layout: hostLayout = 'register',
    cols,
    footerLeading,
    submitLabel,
    submittingLabel,
    submitting,
    isSaving,
    onCancel,
    mode,
    hints = true,
    amountNote = null,
}: InvestmentTxnRowEditProps) {
    const dialog = hostLayout === 'dialog';
    // The fields' touch size in a narrow dialog (components/ui/fieldSize).
    const fieldSize = dialog ? NARROW_DIALOG_FIELD : '';
    // A form row: its fields side by side, wrapping; one per row in a narrow
    // dialog.
    const formRow = dialog ? '@max-3xl/dialog:flex-col @max-3xl/dialog:items-stretch' : undefined;
    const queryClient = useQueryClient();
    const actionId = useId();
    const dateId = useId();
    const taxDateId = useId();
    const checkNumberId = useId();
    const payeeId = useId();
    const tagsId = useId();
    const memoId = useId();
    const memoFromId = useId();
    const tagsFromId = useId();
    const draftHook = useInvestmentTxnDraft({
        brokerageAccountId,
        // Both modes can seed the draft: 'edit' always inverts the
        // existing header's legs; 'new' optionally pre-fills from a
        // Duplicate source (undefined for a plain "+ New", which starts
        // blank). `initialDraft` lives on both arms, so this reads it
        // without narrowing on `kind`.
        initial: mode.initialDraft,
    });
    const {
        draft, errors, isValid: isDraftValid,
        setAction, setPostedAt, setTransactedAt, setPayee, setMemo, setCheckNumber,
        setTags,
        setSecurityId, setShares, setPrice, setAmount,
        setCategoryAccountId, setTransferAccountId, setTransferAmount,
        setFeeAccountId, setFeeAmount,
    } = draftHook;

    // What the editor opened with. An edit sends memo and tags only when they
    // differ from it, so an untouched save writes neither.
    const [opened] = useState(() => ({
        memo: mode.initialDraft?.memo ?? '',
        tags: mode.initialDraft?.tags ?? [],
    }));
    // The money figures the editor opened with: a save rounds a figure the user changed
    // at the brokerage currency's places, as its blur would have (a save by Enter
    // leaves no blur behind it), and sends one left as it opened as stored, so a stored
    // figure finer than its currency is kept (ADR-0114 D15).
    const [openedMoney] = useState(() => ({
        amount: mode.initialDraft?.amount ?? null,
        feeAmount: mode.initialDraft?.feeAmount ?? null,
        transferAmount: mode.initialDraft?.transferAmount ?? null,
    }));
    // The tag limit measures growth from what is stored. A create stores
    // nothing yet, so it is held to the limit itself; so is a host's create
    // (a reminder or an occurrence), unless it edits a stored template.
    const isCreate = mode.kind === 'new' || (mode.kind === 'fire' && mode.initialIsStored !== true);
    const tagBaseline = isCreate ? 0 : opened.tags.length;
    const tagsWithinLimit = tagCountAccepted(tagBaseline, draft.tags.length);

    // Securities are needed for the SecurityField picker.
    // Pre-fetched once per ledger; React Query caches.
    const securitiesQuery = useQuery({
        queryKey: ['securities', ledgerId],
        queryFn: () => fetchSecurities(ledgerId),
        staleTime: 60_000,
    });

    // Holdings for the brokerage account being edited — drives the
    // "holdings-first" ordering in the security picker. The 95%
    // case is "Buy more of what's held here", so those bubble up.
    // Held today (the portfolio card's positions, migration 259: nothing the
    // register shows as Scheduled): a security held only through a
    // future-dated buy is not bubbled up, one sold only by a future-dated
    // sale still is. Ordering only.
    const holdingsQuery = useQuery({
        queryKey: ['holdings', ledgerId, brokerageAccountId],
        queryFn: () => fetchHoldings(ledgerId, brokerageAccountId),
        staleTime: 60_000,
    });
    const holdingsSecurityIds = useMemo(
        () => new Set(
            holdingsQuery.data?.positions.map((p) => p.securityId) ?? [],
        ),
        [holdingsQuery.data],
    );

    // ADR-0043: the brokerage's most-used counterparty accounts +
    // categories, pinned to the top of the Category / Transfer / Fee
    // pickers. One query per editor open, shared across the fields.
    const frequentQuery = useQuery({
        queryKey: ['frequent-counterparties', ledgerId, brokerageAccountId],
        queryFn: () => fetchFrequentCounterparties(ledgerId, brokerageAccountId),
        staleTime: 60_000,
    });

    // Merge candidates: settled rows the edited (fresh,
    // needs_review) row could fold into. Edit-mode only; an empty result
    // hides the panel (the matching predicate decides). Mirrors the bank
    // editor's merge-candidates query.
    const editHeaderId = mode.kind === 'edit' ? mode.headerId : null;
    // Neither strip when the host turns the hints off (`hints`).
    const mergeHeaderId = hints ? editHeaderId : null;
    const mergeCandidatesQuery = useQuery({
        queryKey: ['investment-merge-candidates', ledgerId, mergeHeaderId],
        queryFn: () => fetchInvestmentMergeCandidates(ledgerId, mergeHeaderId!),
        enabled: mergeHeaderId !== null,
        staleTime: Infinity,
    });
    // The ledger's payee vocabulary, for the Payee typeahead. Same query key
    // and staleTime as the bank editor's, so the two share one cache entry
    // rather than each holding their own copy of the same list.
    //
    // Only the TYPEAHEAD is mirrored so far. Bank's similar-payees recall panel
    // — which suggests the (payee, counterparty) pair chosen last time — is NOT
    // yet here, and the reason is scope, not domain: it applies perfectly well.
    // CategoryAccountId is REQUIRED on dividend_cash / dividend_reinvest / divx
    // / misc and FeeAccountId adds one on any action, so a dividend carries an
    // income category leg exactly as a bank row carries its category. Recall is
    // tracked as outstanding rather than declined.
    const payeesQuery = useQuery({
        queryKey: ['payees', ledgerId],
        queryFn: () => fetchPayees(ledgerId),
        staleTime: 30_000,
    });

    // The ledger's tag dictionary, for the TagSelect list. Same query
    // key as the bank editor's, so both editors share one cache entry and a tag
    // created from either surface autocompletes in the other without a refetch.
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
        staleTime: 30_000,
    });

    // Similar-payees recall (slice 2c.6c), the same endpoint and the same query
    // key the bank editor uses — so opening a row in either editor warms the
    // other's cache and the two can never disagree about what was recalled.
    //
    // The endpoint never needed a change for this: a feed row lands BANK-shape
    // whatever account it is bound for — two legs, (real account,
    // Uncategorized), with the investment detail parked in the ingest_*
    // carriers until someone classifies it — so its anchor resolves here
    // exactly as it does on a checking account. This editor simply never
    // asked, and a dividend categorised the same way every quarter had to be
    // categorised by hand every quarter.
    const recallHeaderId = hints && mode.kind === 'edit' ? mode.headerId : null;
    const similarPayeesQuery = useQuery({
        queryKey: ['similar-payees', ledgerId, recallHeaderId],
        queryFn: () => fetchSimilarPayees(ledgerId, recallHeaderId!),
        enabled: recallHeaderId !== null,
        staleTime: Infinity, // static list; no refetch while the editor is open
    });

    // Account-id -> full slash path, so a recalled counterparty reads as its
    // parent->child chain rather than a bare leaf name. Built from the account
    // list the editor already takes, rather than adding a prop the two call
    // sites would both have to thread through.
    const accountPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);
    // The brokerage's currency, the one every amount in this editor is in: the
    // host's, else the account list's. Null while neither has it (the list still
    // loading, or without the brokerage): no figure is printed until it is known.
    const currency = hostCurrency
        ?? accounts.find((a) => a.id === brokerageAccountId)?.currencyCode
        ?? null;

    // The second amount, the transfer account's own figure, while that account is a real
    // account in another currency (ADR-0114 D13, D14). Required and nonzero then; a stored
    // pair left exactly as it opened is not re-judged, as the server checks only what a
    // write changes. A create stores nothing to compare with, so a copied zero pair (a
    // Duplicate, or Post on a reminder) is judged as the server judges it.
    const transferSide: SecondAmountSide | null = transferAmountSide({
        action: draft.action,
        transferAccountId: draft.transferAccountId,
        amount: draft.amount,
        accounts,
        currency,
        accountName: (id) => accountPaths.get(id) ?? accounts.find((a) => a.id === id)?.name ?? id,
    });
    const transferAmountUntouched = !isCreate
        && draft.transferAmount === openedMoney.transferAmount
        && draft.amount === openedMoney.amount;
    const transferAmountError = transferSide !== null
        && (draft.transferAmount == null || (draft.transferAmount === 0 && !transferAmountUntouched))
        ? secondAmountRequired(transferSide)
        : null;
    const allErrors = transferAmountError === null ? errors : { ...errors, transferAmount: transferAmountError };
    const isValid = isDraftValid && tagsWithinLimit && transferAmountError === null;

    // The inheritance hint: a one-posting row on this brokerage whose memo or
    // tags are the other side's shows them as the fields' placeholder, while
    // the draft still saves them as the other side's: one posting across from
    // the account they are on (inheritedAfterSave.ts).
    const inherited = mode.kind === 'edit' ? inheritedAfterSave(mode.inherited ?? null, draft) : null;
    const inheritedFrom = inherited?.fromAccountId
        ? accountPaths.get(inherited.fromAccountId)
            ?? accounts.find((a) => a.id === inherited.fromAccountId)?.name
            ?? null
        : null;
    // Emptying a field is the way back to the other side's value: the field
    // then inherits again, and says so.
    const memoInheriting = inherited !== null
        && inherited.memo !== null
        && normalizeMemo(draft.memo) === null;
    const tagsInheriting = inherited !== null
        && inherited.tags.length > 0
        && draft.tags.length === 0;

    // Armed merge target — set when the user clicks a merge candidate chip,
    // sent on save to the merge route (POST …/merge), never as a PATCH. Null =
    // no merge. The chip is a toggle; the candidate survives as it was
    // entered, so nothing is copied into the form.
    const [mergeFromHeaderId, setMergeFromHeaderId] = useState<string | null>(null);

    const [saveError, setSaveError] = useState<string | null>(null);
    // "+ Create new security" inline flow: state lives here so it
    // survives renderField re-mounts. The query the user typed pre-
    // fills the dialog's Ticker (3-5 chars) or Name field; on
    // creation the dialog returns the new id and we select it.
    const [createDialog, setCreateDialog] =
        useState<{ open: false } | { open: true; query: string }>({ open: false });

    // Shared post-save invalidation: the TanStack-Query-backed
    // surfaces (accounts / holdings / securities) re-fetch; the
    // register isn't a TanStack query so the parent's onCreated /
    // onSaved handler is responsible for refreshing it.
    function invalidateAfterSave() {
        queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
        // The home screen reads what this save moved (lib/registerInvalidation).
        invalidateHomeReads(queryClient, ledgerId);
        // The payee vocabulary is per-LEDGER, not per-register, so a payee first
        // used on a brokerage row belongs in it. Only the bank register was
        // invalidating this key, so the effect was one-directional and easy to
        // miss: a bank save refreshed the list for every editor, an investment
        // save refreshed it for none — the new name stayed absent from every
        // typeahead until the cache expired or the page reloaded.
        queryClient.invalidateQueries({ queryKey: ['payees', ledgerId] });
        queryClient.invalidateQueries({ queryKey: ['holdings'] });
        queryClient.invalidateQueries({ queryKey: ['securities', ledgerId] });
        // Lots may have changed for sell-side actions; invalidate
        // any cached open-lots queries so the next editor open
        // re-fetches.
        queryClient.invalidateQueries({ queryKey: ['open-lots', ledgerId] });
        // Merge candidates are cached with staleTime: Infinity, so nothing else
        // refetches them, and a candidate that has since been merged or accepted
        // would stay on offer until a reload. The bank register invalidates this
        // on every save too. One line here rather than two, because both mutations
        // funnel through this hook.
        queryClient.invalidateQueries({ queryKey: ['investment-merge-candidates', ledgerId] });
        // Recall shares its key with the bank editor and is cached with
        // staleTime: Infinity too. An accepted row is a new (payee, category)
        // datapoint, and the bank register refreshes the family on every save;
        // an investment save refreshed none of it, so a recall stayed stale in
        // both editors until a reload.
        queryClient.invalidateQueries({ queryKey: ['similar-payees', ledgerId] });
        // This editor SEEDS its draft from the ['header-legs', headerId]
        // cache (fetchHeaderLegs — the full cross-account leg set that
        // legsToDraft needs but the windowed register row doesn't carry),
        // and useInvestmentTxnDraft captures `initial` ONCE. A save
        // wholesale-replaces this header's legs (ADR-0025), so that seed is
        // now stale — drop it in the same post-save hook that refreshes the
        // editor's other dependent caches, so the next open re-fetches the
        // saved legs. Without this, reopening the just-saved row re-seeds
        // from the pre-save legs and shows the OLD value (e.g. the amount on
        // a misc txn), which no background refetch can correct (capture-once).
        // Edit mode only: a create has no prior header-legs entry.
        if (mode.kind === 'edit') {
            queryClient.removeQueries({
                queryKey: ['header-legs', ledgerId, mode.headerId],
            });
        }
    }

    const createMutation = useMutation({
        mutationFn: (body: CreateInvestmentTransactionRequest) =>
            createInvestmentTransaction(ledgerId, body),
        onSuccess: (response) => {
            setSaveError(null);
            invalidateAfterSave();
            if (mode.kind === 'new') mode.onCreated(response.headerId);
        },
        onError: (err) => {
            setSaveError(investmentSaveErrorWords(err, transferSide));
        },
    });

    const patchMutation = useMutation({
        mutationFn: (body: PatchInvestmentTransactionRequest) => {
            if (mode.kind !== 'edit') {
                return Promise.reject(new Error('patchMutation in non-edit mode'));
            }
            // A merge is a command with its own route — see bank-edit/save.ts
            // for the same dispatch. handleSave sends a merge-only body, so the field's
            // presence is the signal; the PATCH contract no longer accepts it.
            return body.mergeFromHeaderId
                ? mergeInvestmentTransaction(
                      ledgerId, mode.headerId, body.mergeFromHeaderId, brokerageAccountId)
                : patchInvestmentTransaction(
                      ledgerId, mode.headerId, body, brokerageAccountId);
        },
        onSuccess: (entries, body) => {
            setSaveError(null);
            invalidateAfterSave();
            // The merge target rides along: on a merge the edited row is the
            // LOSER and `entries` are the survivor's, which the page cannot
            // work out from the entries by themselves.
            if (mode.kind === 'edit') {
                mode.onSaved(entries, body.mergeFromHeaderId ?? null);
            }
        },
        onError: (err) => {
            setSaveError(investmentSaveErrorWords(err, transferSide));
        },
    });

    /// Escape cancels and Enter saves, as they do on a bank row.
    function handleKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
        // A dialog this editor opened (Add security) renders inside it, so its
        // keys bubble through here. They are that dialog's: Enter there submits
        // its form, and taking it here would save this transaction instead. A
        // dialog inside an editor is one an editor opened; the dialog this
        // editor may sit in holds the editor, so the key is still the editor's.
        if (happenedInside(event.nativeEvent, EDITOR_DIALOG)) return;
        const target = event.target as HTMLElement | null;

        // `defaultPrevented` is the guard, copied deliberately from the bank
        // editor: a picker that closed its own panel on Escape marks the event
        // (and stops it), and without the check a control that only marked it
        // would close the panel and then throw away the whole transaction behind
        // it on the same keystroke.
        //
        // While a save is pending the Escape is marked and refused, as the
        // pending Cancel it stands for refuses a press: the pending Save keeps
        // focus in the editor, so the key does reach here then. The mark keeps a
        // dialog this editor sits in open too.
        if (event.key === 'Escape' && !event.defaultPrevented) {
            event.preventDefault();
            if (pending) return;
            onCancel();
            return;
        }

        // Enter saves. The bank editor has had this on its text fields; here it
        // sits on the container so it works from any field, and the same
        // exclusions apply:
        //
        //   * defaultPrevented — a picker that consumed Enter to choose an option
        //     must not also submit the row behind it.
        //   * Shift+Enter — a newline in the memo textarea.
        //   * a textarea at all — Enter is a newline there, which is why the bank
        //     editor pairs it with the Shift check on that field specifically.
        //   * a button — Enter on a focused button is that button's click, and
        //     hijacking it would fire Save from Cancel.
        if (event.key !== 'Enter' || event.shiftKey || event.defaultPrevented) return;
        const tag = target?.tagName;
        if (tag === 'TEXTAREA' || tag === 'BUTTON') return;

        event.preventDefault();
        // Press Save rather than calling handleSave: Save refuses an invalid
        // draft (disabled) and a save in flight (pending), and its press holds
        // it until the save it started has settled (ADR-0023 §L), so a second
        // Enter before the pending state renders sends nothing.
        saveRef.current?.click();
    }

    function handleSave() {
        // Merge fold (mirrors bank): when a candidate is armed the editor's
        // fields are moot — send the merge alone, which the save mutation
        // routes to POST …/merge, and skip the validity gate. The edited row
        // becomes the loser; the candidate survives.
        // A save error stays shown until this save's result replaces or clears
        // it (onSuccess, onError), never at the press (ADR-0023 §L).
        if (mode.kind === 'edit' && mergeFromHeaderId !== null) {
            patchMutation.mutate({ mergeFromHeaderId });
            return;
        }
        if (!isValid || !draft.action) return;
        const sent = {
            ...draft,
            amount: atPlacesIfChanged(draft.amount, openedMoney.amount, currency),
            feeAmount: atPlacesIfChanged(draft.feeAmount, openedMoney.feeAmount, currency),
        };
        // The transfer account's own leg, signed, on a transfer between currencies; left
        // out of the body otherwise (ADR-0114 D13).
        const magnitude = transferSide === null ? null
            : atPlacesIfChanged(draft.transferAmount ?? null, openedMoney.transferAmount, transferSide.currency);
        const transferAmount = transferSide === null || magnitude === null
            ? null
            : signedSecondAmount(magnitude, transferSide.direction);
        try {
            if (mode.kind === 'new') {
                createMutation.mutate(draftToCreateRequest(sent, transferAmount));
            } else if (mode.kind === 'fire') {
                // Adjust-at-post: hand the create-shaped request to the modal
                // host (which POSTs the fire endpoint); no internal mutation.
                // A stored template carries only the memo and tags that changed.
                mode.onSubmit(mode.initialIsStored === true
                    ? draftToStoredRequest(sent, opened, transferAmount)
                    : draftToCreateRequest(sent, transferAmount));
            } else {
                // ADR-0031 Phase 3d.2: when this edit started from a
                // sync hint AND the user resolved a security, pass
                // the provider hint back so the server records the
                // (ticker → security) mapping for future syncs.
                // Server ignores the hint when SecurityId isn't set
                // on the request, so this is safe to pass through
                // even on actions that don't need a security.
                const body = draftToPatchRequest(sent, opened, transferAmount);
                // Saving a needs-review row IS the Accept action, so the flag
                // rides along and the row is accepted in the same transaction
                // as the edit. The server does not infer it: clearing needs_review
                // on every successful PATCH would make every other client accept
                // whatever it touches, and nobody could correct an imported row
                // and leave it queued. An
                // already-accepted row omits the field, so a no-op re-save
                // stays semantically idempotent — same shape as the bank editor.
                if (mode.needsReview === true) {
                    body.approve = true;
                }
                if (mode.providerSecurityHint && draft.securityId !== null) {
                    body.providerSecurityHint = mode.providerSecurityHint;
                }
                patchMutation.mutate(body);
            }
        } catch (e) {
            setSaveError(e instanceof Error ? e.message : 'Save failed.');
        }
    }

    const mutation = mode.kind === 'new' ? createMutation : patchMutation;

    // Save's label at rest, and while its own save is in flight. PendingLabel
    // lays both out from the first render, so Save never changes size.
    const isMergingSave = mergeFromHeaderId !== null;
    const isAcceptingSave = mode.kind === 'edit' && mode.needsReview === true;
    const saveLabel = isMergingSave ? 'Fold into selected →'
        : isAcceptingSave ? 'Accept' : (submitLabel ?? 'Save');
    const savingLabel = isMergingSave ? 'Folding…'
        : isAcceptingSave ? 'Accepting…' : (submittingLabel ?? 'Saving…');
    // Enter in a field presses Save (handleKeyDown, the memo), so the press
    // holds it as a click does (ADR-0023 §L).
    const saveRef = useRef<HTMLButtonElement | null>(null);

    const action = draft.action;
    const layout = action ? ACTION_LAYOUTS[action] : [];
    // Recall is offered on EVERY action. It was gated to actions with a
    // category slot, on the reasoning that a suggestion is one (payee,
    // counterparty) pair and there is nowhere to put the counterparty half of
    // one on a buy or a sell. That threw away the half that matters: the payee
    // is the repeated thing, and on a brokerage the row carrying the name the
    // user curated is very often a buy.
    const recallSuggestions = similarPayeesQuery.data ?? [];

    // The Holdings sub-accounts in this ledger. A prior settled buy's
    // counterparty IS one (ADR-0019) — structural, not a category, and offered
    // by no picker — so its half of the pair cannot be applied.
    const holdingsAccountIds = useMemo(
        () => new Set(
            accounts
                .map((a) => a.holdingsAccountId)
                .filter((id): id is string => id !== null),
        ),
        [accounts],
    );

    // Whether a suggestion's counterparty half can be taken: the action must
    // have a category slot to put it in, and it must be something that slot
    // accepts.
    const canApplyCounterparty = (counterpartyAccountId: string) =>
        layout.includes('category')
        && !holdingsAccountIds.has(counterpartyAccountId);
    // The host's flag counts too — in 'fire' mode it is the only one that can
    // ever be true. While a save is pending the fields are disabled, and the
    // buttons are pending: they keep focus and refuse to act (pending.ts).
    const pending = mutation.isPending || isSaving === true;

    return (
        <FieldSizeContext.Provider value={fieldSize}>
        <div
            // A register row in the register; in a dialog there is no grid to
            // be a row of, so it is a named group, without the editing-row band.
            role={dialog ? 'group' : 'row'}
            aria-label={dialog ? 'Investment transaction' : 'New investment transaction'}
            onKeyDown={handleKeyDown}
            data-editing="true"
            data-creating={mode.kind === 'new' || undefined}
            style={dialog ? undefined : { gridTemplateColumns: cols }}
            // Editing-row styling mirrors the bank register's
            // TxnRowEdit container: accent borders top + bottom and
            // a soft accent background so the active row stands
            // apart from the surrounding rows. A dialog's body is the
            // frame instead: one fluid column, no band, no inset of its own.
            className={dialog
                ? 'grid grid-cols-[minmax(0,1fr)] items-start gap-2 py-2'
                : 'grid items-start gap-2 border-y border-accent/40 bg-accent-soft/10 px-3 py-2'}
        >
            {/* Row 1: action picker + dates + check#. Spans the full grid via a
                nested flex so the fields row below can use the register's column
                template. Labels sit ABOVE their fields, matching every other row
                in this editor. */}
            <div className={cn('col-span-full flex flex-wrap items-end gap-3 pb-2', formRow)}>
                <div className="flex flex-col gap-1 text-xs">
                    <FieldLabel htmlFor={actionId}>Action</FieldLabel>
                    <select
                        id={actionId}
                        value={action ?? ''}
                        aria-label="Action"
                        className={cn(
                            'h-control-28px rounded border border-border bg-surface px-2 text-xs',
                            'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                            fieldSize,
                        )}
                        {...pendingChoiceProps<HTMLSelectElement>(pending, { onChange: (e) => {
                            const next = (e.target.value || null) as LedgerInvestmentAction | null;
                            setAction(next);
                            // Re-derive Amount sign per the new action's
                            // convention so a sync-imported row pre-fills
                            // the editable field on action pick. abs-using
                            // actions normalize draft.amount; signed-using
                            // actions leave it as-is (the user edits sign
                            // explicitly for dividend_cash / transfer).
                            if (draft.amount !== null) {
                                setAmount(amountForAction(draft.amount, next));
                            }
                        } })}
                    >
                        <option value="">Pick action…</option>
                        {ACTION_PICKER_ENTRIES.map(({ action: a, label, hint }) => (
                            <option key={a} value={a} title={hint}>
                                {label}
                            </option>
                        ))}
                    </select>
                </div>

                {/* The shared DateField, not a hand-rolled type="date".
                    Both editors were spelling the same input twice, and only
                    one of the two copies had the keyboard shortcuts — so `t`,
                    `y` and +/- worked on a bank row and silently did nothing on
                    an investment one. autoFocus is preserved: without it focus
                    stays on the register grid behind and the first keystroke
                    after opening a row goes nowhere. */}
                <div className="text-xs">
                    <DateField
                        id={dateId}
                        label="Date"
                        value={draft.postedAt}
                        onChange={setPostedAt}
                        autoFocus
                        pending={pending}
                    />
                </div>

                {/* Tax date. Blank = same as the posted date; the payload builder
                    sends postedAt for blank rather than null, because the
                    investment PATCH is wholesale-replace and an omitted field
                    CLEARS the stored value. */}
                {/* "(optional)" goes in the LABEL, following Fee: a
                    `placeholder` is inert on type="date" — the browser's own
                    mm/dd/yyyy mask occupies that space — so the text inputs'
                    placeholder convention cannot carry it here. The hint rides
                    in DateField's tooltip alongside the shortcut list. */}
                <div className="text-xs">
                    <DateField
                        id={taxDateId}
                        label="Tax date (optional)"
                        value={draft.transactedAt}
                        onChange={setTransactedAt}
                        pending={pending}
                        hint="Leave blank when the tax date is the same as the posted date"
                    />
                </div>

                <div className="flex flex-col gap-1 text-xs">
                    <FieldLabel htmlFor={checkNumberId}>Check #</FieldLabel>
                    <input
                        id={checkNumberId}
                        type="text"
                        value={draft.checkNumber}
                        onChange={(e) => setCheckNumber(e.target.value)}
                        placeholder="(optional)"
                        className={cn(
                            'h-control-28px w-fixed-96px rounded border border-border bg-surface px-2 text-xs',
                            'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                            fieldSize,
                            dialog && '@max-3xl/dialog:w-full',
                        )}
                        {...pendingTextProps<HTMLInputElement>(pending)}
                    />
                </div>

            </div>

            {/* Row 2: payee + memo (header-level, optional on every action). */}
            <div className={cn('col-span-full flex gap-3 pb-2', formRow)}>
                <div className="flex min-w-0 flex-1 flex-col gap-1 text-xs">
                    <FieldLabel htmlFor={payeeId}>Payee</FieldLabel>
                    {/* The same Typeahead the bank editor uses, over the same
                        ledger-wide payee list. With a bare text input, a payee
                        typed on a brokerage row has to be retyped exactly or it
                        silently becomes a second, near-identical entry in a
                        vocabulary that is ledger-wide, not per-register. */}
                    <Typeahead<PayeeSuggestion>
                        items={payeesQuery.data ?? []}
                        value={draft.payee}
                        onChange={setPayee}
                        getKey={(p) => p.name}
                        getLabel={(p) => p.name}
                        pending={pending}
                        aria-label="Payee"
                    />
                    {/* Recall chips, under the payee exactly as on a bank row.
                        Offered on every action: the payee is the repeated
                        thing, and it is applicable whatever the shape.

                        The counterparty half is applied only when there is a
                        category slot to put it in AND it is not a Holdings
                        sub-account — and the chip hides its "→ X" in exactly
                        the cases it will not apply it, so a chip never claims
                        to do something it then skips.

                        The fee slot is also a category counterparty, but which
                        of the two a suggestion meant is not recoverable from
                        the pair, so it lands on the category and the user moves
                        it if they meant the fee. */}
                    {recallSuggestions.length > 0 ? (
                        <SimilarPayeesPanel
                            suggestions={recallSuggestions}
                            accountPaths={accountPaths}
                            pending={pending}
                            dialog={dialog}
                            showsCounterparty={(s) =>
                                canApplyCounterparty(s.counterpartyAccountId)
                            }
                            onApply={(s) => {
                                setPayee(s.payee);
                                if (canApplyCounterparty(s.counterpartyAccountId)) {
                                    setCategoryAccountId(s.counterpartyAccountId);
                                }
                            }}
                        />
                    ) : null}
                </div>
                <div className="flex min-w-0 flex-[2] flex-col gap-1 text-xs">
                    <NotesFieldCaption
                        htmlFor={memoId}
                        fromId={memoFromId}
                        label="Memo"
                        otherSide={inheritedFrom}
                        inheriting={memoInheriting}
                    />
                    <span className="relative block">
                    {memoInheriting ? (
                        <InheritedMarker
                            from={inheritedFrom}
                            className="pointer-events-none absolute left-2 top-[0.5625rem]"
                        />
                    ) : null}
                    <input
                        id={memoId}
                        type="text"
                        value={draft.memo}
                        onChange={(e) => setMemo(e.target.value)}
                        // Enter commits, as it does from the bank editor's memo.
                        // There is no <form> here and no submit button, so without
                        // this the only way to save was to reach for the mouse.
                        // Guarded on validity for the same reason the button is
                        // disabled — Enter must not do what the button refuses.
                        onKeyDown={(e) => {
                            if (e.key !== 'Enter' || e.shiftKey) return;
                            e.preventDefault();
                            saveRef.current?.click();
                        }}
                        aria-describedby={memoInheriting ? memoFromId : undefined}
                        placeholder={memoInheriting ? inherited!.memo! : '(optional)'}
                        className={cn(
                            'h-control-28px w-full rounded border border-border bg-surface pr-2 text-xs placeholder:text-text-subtle',
                            'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                            memoInheriting ? 'pl-6' : 'pl-2',
                            fieldSize,
                        )}
                        {...pendingTextProps<HTMLInputElement>(pending)}
                    />
                    </span>
                </div>
            </div>

            {/* Row 2b: tags (ADR-0009 — a property of the EVENT, so it applies
                to every action and sits with the header-level fields rather
                than among the action x field matrix below).

                Same component, same ledger dictionary and same create-on-
                first-use semantics as the bank editor, so a tag can reach an
                investment header from this surface, not only through the MCP
                bulk tool. One control for the whole event — the legs the
                action derives never carry their own, unlike a bank split's
                postings, which gained theirs in ADR-0103. */}
            <div className="col-span-full flex flex-col gap-1 pb-2 text-xs">
                <NotesFieldCaption
                    htmlFor={tagsId}
                    fromId={tagsFromId}
                    label="Tags"
                    otherSide={inheritedFrom}
                    inheriting={tagsInheriting}
                />
                <TagSelect
                    id={tagsId}
                    tags={draft.tags}
                    placeholderTags={inherited?.tags}
                    placeholderFrom={inheritedFrom}
                    maxCount={tagCountCeiling(tagBaseline)}
                    allTags={tagsQuery.data ?? []}
                    onChange={setTags}
                    pending={pending}
                    aria-label="Tags"
                    aria-describedby={tagsInheriting ? tagsFromId : undefined}
                />
                {!tagsWithinLimit ? (
                    <p role="alert" className="text-[0.6875rem] text-state-danger">
                        This tag set can't grow past {TAG_LIMIT} tags.
                    </p>
                ) : null}
            </div>

            {/* Merge candidates — settled rows this fresh row could fold
                into (self-hides when there are none). Directly under Payee/
                Memo, mirroring the bank editor's placement. Its chips carry
                amounts, so it waits for the brokerage's currency. */}
            {currency !== null ? (
                <InvestmentMergeCandidatesPanel
                    candidates={mergeCandidatesQuery.data ?? []}
                    accountPaths={accountPaths}
                    currency={currency}
                    selectedHeaderId={mergeFromHeaderId}
                    pending={pending}
                    dialog={dialog}
                    onSelect={(c) =>
                        setMergeFromHeaderId((cur) => (cur === c.headerId ? null : c.headerId))
                    }
                />
            ) : null}

            {/* Row 3: action-specific fields, ordered by ACTION_LAYOUTS. */}
            {action ? (
                <>
                    <div className={cn('col-span-full flex flex-wrap gap-3', formRow)}>
                        {layout.map((key) => renderField(key, {
                            action,
                            draft,
                            errors,
                            accounts,
                            frequent: frequentQuery.data ?? null,
                            securities: securitiesQuery.data ?? [],
                            holdingsSecurityIds,
                            brokerageAccountId,
                            currency,
                            ledgerId,
                            transferSide,
                            transferAmountError,
                            setTransferAmount,
                            amountNote,
                            isTradeCommission,
                            pending,
                            onCreateSecurity: (query) =>
                                setCreateDialog({ open: true, query }),
                            setSecurityId,
                            // Tri-field link on the buy / sell / buyx / sellx /
                            // dividend-reinvest family. AMOUNT is authoritative
                            // (the real money paid/received, at the brokerage
                            // currency's places, ADR-0114 D15); PRICE is
                            // derived metadata (amount ÷ shares); SHARES is the
                            // signed quantity (sells are negative). Each edit
                            // holds one field and derives a third so the
                            // invariant amount = shares × price stays consistent,
                            // and the server (which derives unit_price =
                            // amount ÷ |shares|) persists exactly what's shown
                            // (ADR-0073):
                            //   * edit amount → price  = |amount| ÷ |shares|
                            //   * edit shares → amount = |shares| × |price|, rounded
                            //       once at the currency's places (none while the
                            //       currency is not known)
                            //   * edit price  → shares = amount ÷ price (sign
                            //       kept) — or, on a fresh row with no amount
                            //       yet, bootstraps amount = |shares| × |price|
                            //       so "N shares @ $P" manual entry still works.
                            // MAGNITUDES throughout: sells store shares NEGATIVE
                            // while Amount / Price are non-negative; the old
                            // `> 0` guard silently skipped the whole link on
                            // every sell.
                            setAmount: (next) => {
                                setAmount(next);
                                if (isLinkedAction(action)
                                    && next !== null
                                    && draft.shares !== null
                                    && draft.shares !== 0
                                ) {
                                    setPrice(roundTo(
                                        Math.abs(next) / Math.abs(draft.shares), 6));
                                }
                            },
                            setShares: (next) => {
                                setShares(next);
                                if (isLinkedAction(action)
                                    && next !== null
                                    && next !== 0
                                    && draft.price !== null
                                ) {
                                    setAmount(roundToMinorUnits(
                                        Math.abs(next) * Math.abs(draft.price), currency));
                                }
                            },
                            setPrice: (next) => {
                                setPrice(next);
                                if (!isLinkedAction(action) || next === null || next === 0) {
                                    return;
                                }
                                if (draft.amount !== null && draft.amount !== 0) {
                                    // Amount is authoritative — hold it fixed and
                                    // back-solve the share count, keeping the
                                    // action's sign (sells stay negative).
                                    const sign =
                                        draft.shares !== null && draft.shares < 0 ? -1 : 1;
                                    setShares(roundTo(
                                        sign * (Math.abs(draft.amount) / Math.abs(next)), 6));
                                } else if (draft.shares !== null && draft.shares !== 0) {
                                    // Fresh row, no amount yet — bootstrap it from
                                    // shares × price so manual entry works.
                                    setAmount(roundToMinorUnits(
                                        Math.abs(draft.shares) * Math.abs(next), currency));
                                }
                            },
                            setCategoryAccountId,
                            setTransferAccountId,
                            setFeeAccountId,
                            setFeeAmount,
                        }))}
                    </div>
                    {/* A4.c.4: FIFO consumption preview. Renders only
                        on sell / sellx with a security + shares value;
                        component handles visibility internally. */}
                    <div className="col-span-full">
                        <FifoPreviewPopover
                            action={action}
                            brokerageAccountId={brokerageAccountId}
                            ledgerId={ledgerId}
                            securityId={draft.securityId}
                            sharesInput={draft.shares}
                            postedAt={draft.postedAt}
                            excludeHeaderId={editHeaderId}
                            currency={currency}
                        />
                    </div>
                </>
            ) : (
                // No action picked yet — surface the bank-given
                // amount as read-only so the user has the sign +
                // magnitude in view while choosing an action. The
                // editable AmountField appears once an action is
                // selected (the per-action layout owns sign rules).
                <div className="col-span-full flex items-baseline gap-3 px-1 py-2 text-xs">
                    <span className="text-text-subtle uppercase tracking-wide">Amount</span>
                    <span className={`font-mono tabular-nums ${
                        // text-state-danger, not text-danger: index.css is Tailwind
                        // v4 CSS-first, so only declared --color-* names become
                        // utilities and --color-danger is not one. The class was
                        // inert, which left a negative amount in ordinary body text
                        // at exactly the moment its sign is what tells the user
                        // which action to pick.
                        (draft.amount ?? 0) < 0 ? 'text-state-danger' : ''
                    }`}>
                        {draft.amount === null
                            ? '—'
                            : currency === null
                                ? <Skeleton className="inline-block h-fixed-12px w-fixed-80px align-middle" />
                                : formatCurrency(draft.amount, currency)}
                    </span>
                    <span className="text-text-subtle">— pick an action to see its fields.</span>
                </div>
            )}

            {createDialog.open ? (
                <AddSecurityDialog
                    ledgerId={ledgerId}
                    onClose={() => setCreateDialog({ open: false })}
                    onCreated={(newId) => {
                        setCreateDialog({ open: false });
                        // Refetch the catalog so the picker sees the
                        // new row; once it lands we select it.
                        queryClient.invalidateQueries({
                            queryKey: ['securities', ledgerId],
                        });
                        setSecurityId(newId);
                    }}
                    // Pre-fill the dialog with whatever the user
                    // typed. Tickers are 1–5 letters; longer queries
                    // are clearly a name search, so route them to the
                    // Name field instead.
                    initialTicker={
                        createDialog.query.length > 0
                            && createDialog.query.length <= 5
                            && /^[A-Za-z.]+$/.test(createDialog.query)
                            ? createDialog.query.toUpperCase()
                            : undefined
                    }
                    initialName={
                        createDialog.query.length > 5
                            || !/^[A-Za-z.]+$/.test(createDialog.query)
                            ? createDialog.query
                            : undefined
                    }
                />
            ) : null}

            {/* Bottom action row — harmonized with the bank editor (ADR-0023):
                an optional leading slot (e.g. the reminders Skip) on the left,
                Cancel + Save on the right, via the shared Button primitive. */}
            <div className={dialog
                ? 'col-span-full flex items-center justify-between gap-2 border-t border-border/30 pt-3 @max-3xl/dialog:flex-col @max-3xl/dialog:items-stretch @max-3xl/dialog:gap-2.5'
                : 'col-span-full -mx-3 flex items-center justify-between gap-2 border-t border-border/30 px-3 pt-2'}>
                <div className={cn('flex items-center gap-3', dialog && '@max-3xl/dialog:justify-between @max-3xl/dialog:empty:hidden')}>
                    {footerLeading}
                </div>
                <div className={cn('flex items-center gap-2', dialog && '@max-3xl/dialog:grid @max-3xl/dialog:grid-cols-2')}>
                    <Button type="button" variant="secondary" size="sm" onClick={onCancel} pending={pending}
                        className={dialog ? '@max-3xl/dialog:h-control-44px' : undefined}>
                        Cancel
                    </Button>
                    <Button
                        ref={saveRef}
                        type="button"
                        variant="primary"
                        size="sm"
                        className={dialog ? '@max-3xl/dialog:h-control-44px' : undefined}
                        onClick={handleSave}
                        // When folding into a merge candidate the editor's
                        // fields are discarded, so Save stays enabled purely
                        // on the merge stamp (mirrors bank). A save in flight
                        // makes it pending, not disabled.
                        disabled={mergeFromHeaderId === null && !isValid}
                        pending={pending}
                        // A disabled control that does not say why is a dead end:
                        // the validator already produces a message per field and
                        // nothing was showing them, so the only feedback was a
                        // button that would not press. Bank has done this since
                        // ADR-0023; same treatment, same source of truth.
                        title={
                            mergeFromHeaderId === null && !isValid
                                ? Object.values(allErrors).filter(Boolean).join('\n')
                                : undefined
                        }
                    >
                        <PendingLabel
                            pending={mutation.isPending || (submitting ?? isSaving === true)}
                            label={saveLabel}
                            pendingLabel={savingLabel}
                        />
                    </Button>
                </div>
            </div>

            {/* The bank editor's error treatment: a bordered block on its own
                line BELOW the action row, not a bare span INSIDE that row,
                between the footer slot and Cancel — where a server message of
                any length fights the buttons for a flex row's width, and the
                thing you need to read is the thing that gets squeezed. */}
            {saveError ? (
                <p
                    role="alert"
                    className={cn(
                        'col-span-full mt-2 rounded border border-state-danger/40 bg-state-danger-soft px-2 py-1 text-[0.6875rem] text-state-danger',
                        !dialog && '-mx-3',
                    )}
                >
                    {saveError}
                </p>
            ) : null}
        </div>
        </FieldSizeContext.Provider>
    );
}

/**
 * Merge-candidates panel for the investment editor (mirrors the bank
 * MergeCandidatesPanel). Each chip reads like the candidate's register row,
 * in the register's order — date · action · payee · ticker · shares · amount —
 * with the register's action label, sign and number format, and a cash
 * transfer's other account after the payee as the bank chips show it. Without
 * the payee and the row's own values, a chip could not be matched to the row
 * it stands for. Toggling one arms/clears the merge. Picking a chip folds the
 * edited (fresh, needs-review) row into that candidate: the candidate is the
 * surviving winner, so there's no form pre-fill. Self-hides when there are no
 * candidates.
 */
function InvestmentMergeCandidatesPanel({
    candidates,
    accountPaths,
    currency,
    selectedHeaderId,
    pending,
    dialog,
    onSelect,
}: {
    candidates: readonly InvestmentMergeCandidate[];
    accountPaths: ReadonlyMap<string, string>;
    /** The brokerage's currency, the one its register formats amounts in. */
    currency: string;
    selectedHeaderId: string | null;
    /** The editor's save is in flight: the chips keep focus and refuse a pick (pending.ts). */
    pending: boolean;
    /** The editor is in its dialog layout: on a narrow dialog the chips
     *  take the touch size (bank-edit/fields/hintChips.ts). */
    dialog: boolean;
    onSelect: (candidate: InvestmentMergeCandidate) => void;
}) {
    const pendingProps = usePendingButtonProps();
    if (candidates.length === 0) return null;
    return (
        <div className={cn(
            'col-span-full flex min-w-0 flex-wrap items-baseline gap-x-1.5 gap-y-1 pb-2 text-[0.625rem]',
            dialog && DIALOG_NARROW_HINT_STRIP,
        )}>
            {/* "Merge candidates", matching the bank editor. It read
                "Possible matches" here, so the word "merge" appeared nowhere on
                the investment side at all — which is most of why merge was
                believed to be a bank-only feature. */}
            <span className="text-text-subtle">Merge candidates:</span>
            {candidates.map((c) => {
                const isSelected = selectedHeaderId === c.headerId;
                // postedAt is UTC-anchored (server treats it as a calendar
                // date); slice the date portion directly.
                const dateLabel = c.postedAt.slice(0, 10);
                // The register's action chip label, a derived one included.
                const action = c.derivedAction ?? c.action;
                const actionLabel = action ? formatInvestmentAction(action) : null;
                // The register shows shares unsigned (the action carries the
                // direction) and amounts signed.
                const sharesLabel = c.shares !== null && c.shares !== undefined
                    ? `${formatShares(Math.abs(c.shares))} sh`
                    : null;
                const counterparty = c.counterpartyAccountId || c.counterpartyAccountName
                    ? displayAccountPath(accountPaths, c.counterpartyAccountId, c.counterpartyAccountName)
                    : null;
                const payeeLabel = c.payee ?? (counterparty ? '(no payee)' : null);
                return (
                    <button
                        key={c.headerId}
                        type="button"
                        aria-pressed={isSelected}
                        title={mergeChipTitle(dateLabel, c.origin)}
                        {...pendingProps(pending, () => onSelect(c))}
                        className={cn(
                            'inline-flex items-baseline gap-1 rounded border px-1.5 py-0.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                            isSelected
                                ? 'border-accent bg-accent-soft text-accent'
                                : 'border-border bg-surface text-text hover:border-accent hover:bg-surface-muted',
                            dialog && DIALOG_NARROW_HINT_CHIP,
                        )}
                    >
                        <span className="text-text-subtle">{dateLabel}</span>
                        {actionLabel ? <span>{actionLabel}</span> : null}
                        {payeeLabel ? <span className="font-medium">{payeeLabel}</span> : null}
                        {counterparty ? (
                            <>
                                <span className="text-text-subtle">→</span>
                                <span>{counterparty}</span>
                            </>
                        ) : null}
                        {c.securityTicker ? (
                            <span className="font-medium">{c.securityTicker}</span>
                        ) : null}
                        {sharesLabel ? (
                            <span className="text-text-subtle">{sharesLabel}</span>
                        ) : null}
                        <span className="font-mono tabular-nums">{formatSignedAmount(c.amount, currency)}</span>
                        {isSelected ? (
                            <span className="text-text-subtle" aria-hidden>✓</span>
                        ) : null}
                    </button>
                );
            })}
        </div>
    );
}

interface FieldRenderProps {
    action: LedgerInvestmentAction;
    draft: ReturnType<typeof useInvestmentTxnDraft>['draft'];
    errors: ReturnType<typeof useInvestmentTxnDraft>['errors'];
    accounts: readonly AccountSummary[];
    /** ADR-0043: the brokerage's most-used counterparties, pinned to
     *  the top of the Category / Transfer / Fee pickers. */
    frequent: import('@/lib/types').FrequentCounterpartiesResponse | null;
    securities: readonly import('@/lib/types').SecuritySummary[];
    /** Subset of `securities` that this brokerage account currently
     *  holds. Drives the picker's "holdings-first" ordering. */
    holdingsSecurityIds: ReadonlySet<string>;
    brokerageAccountId: string;
    /** The brokerage's currency, the one Amount, Fee and Price are typed in (ADR-0114
     *  D14, D15); null while the accounts load. */
    currency: string | null;
    ledgerId: string;
    /** The second amount's account, while it shows (ADR-0114 D14). */
    transferSide: SecondAmountSide | null;
    transferAmountError: string | null;
    setTransferAmount: (magnitude: number | null, fixed: boolean) => void;
    /** Amount's rounded-at-import note, in the Review editor (ADR-0114 D15). */
    amountNote: string | null;
    isTradeCommission: boolean;
    /** The editor's save is in flight: the fields are disabled. */
    pending: boolean;
    /** Opens the inline "+ Create new security" dialog, pre-filled
     *  with the user's typed query. */
    onCreateSecurity: (query: string) => void;
    setSecurityId: (next: string | null) => void;
    setShares: (next: number | null) => void;
    setPrice: (next: number | null) => void;
    setAmount: (next: number | null) => void;
    setCategoryAccountId: (next: string | null) => void;
    setTransferAccountId: (next: string | null) => void;
    setFeeAccountId: (next: string | null) => void;
    setFeeAmount: (next: number | null) => void;
}

/**
 * One-place dispatch from a field key to its component. Per
 * README.md design choice #1, the ACTION × field matrix is data;
 * this renderer just iterates over the per-action key list and
 * mounts the corresponding component.
 */
function renderField(key: FieldKey, p: FieldRenderProps) {
    switch (key) {
        case 'security':
            return (
                <div key={key} className="min-w-[12rem] flex-1">
                    <SecurityField
                        securities={p.securities}
                        valueId={p.draft.securityId}
                        onChangeId={p.setSecurityId}
                        error={p.errors.security ?? null}
                        pending={p.pending}
                        holdingsSecurityIds={p.holdingsSecurityIds}
                        onCreate={p.onCreateSecurity}
                    />
                </div>
            );
        case 'shares':
            return (
                <div key={key} className="min-w-[7rem]">
                    <SharesField
                        action={p.action}
                        value={p.draft.shares}
                        onChange={p.setShares}
                        error={p.errors.shares ?? null}
                        pending={p.pending}
                    />
                </div>
            );
        case 'price':
            return (
                <div key={key} className="min-w-[7rem]">
                    <PriceField
                        value={p.draft.price}
                        currency={p.currency}
                        onChange={p.setPrice}
                        error={p.errors.price ?? null}
                        pending={p.pending}
                    />
                </div>
            );
        case 'amount':
            return (
                <Fragment key={key}>
                    <div className="min-w-[8rem]">
                        <AmountField
                            action={p.action}
                            value={p.draft.amount}
                            currency={p.currency}
                            onChange={p.setAmount}
                            error={p.errors.amount ?? null}
                            pending={p.pending}
                            note={p.amountNote}
                        />
                    </div>
                    {/* Right of Amount, paired with it (on BuyXfr, SellXfr and
                        DividendXfr the trade amount, not the fee), while the transfer
                        account is a real account in another currency (ADR-0114 D14). */}
                    {p.transferSide !== null && p.currency !== null ? (
                        <div className="min-w-[8rem]">
                            <TransferAmountField
                                ledgerId={p.ledgerId}
                                side={p.transferSide}
                                currency={p.currency}
                                date={p.draft.postedAt}
                                amount={p.draft.amount}
                                value={p.draft.transferAmount ?? null}
                                fixed={p.draft.transferAmountFixed === true}
                                onChange={p.setTransferAmount}
                                error={p.transferAmountError}
                                pending={p.pending}
                            />
                        </div>
                    ) : null}
                </Fragment>
            );
        case 'category':
            return (
                <div key={key} className="min-w-[12rem] flex-1">
                    <CategoryField
                        accounts={p.accounts}
                        frequent={p.frequent}
                        valueId={p.draft.categoryAccountId}
                        onChangeId={p.setCategoryAccountId}
                        error={p.errors.category ?? null}
                        pending={p.pending}
                    />
                </div>
            );
        case 'transfer':
            return (
                <div key={key} className="min-w-[12rem] flex-1">
                    <TransferField
                        accounts={p.accounts}
                        frequent={p.frequent}
                        brokerageAccountId={p.brokerageAccountId}
                        valueId={p.draft.transferAccountId}
                        onChangeId={p.setTransferAccountId}
                        error={p.errors.transfer ?? null}
                        pending={p.pending}
                        restrictToInvestment={p.action === 'transfer_shares'}
                    />
                </div>
            );
        case 'fee':
            return (
                <div key={key} className="min-w-[14rem] flex-[1.2]">
                    <FeeField
                        accounts={p.accounts}
                        frequent={p.frequent}
                        feeAccountId={p.draft.feeAccountId}
                        onChangeFeeAccount={p.setFeeAccountId}
                        feeAmount={p.draft.feeAmount}
                        onChangeFeeAmount={p.setFeeAmount}
                        currency={p.currency}
                        error={p.errors.feeAmount ?? null}
                        contextualHint={p.isTradeCommission
                            ? 'Will be added to the lot’s cost basis.'
                            : 'Booked as an expense; cost basis uses share price only.'}
                        pending={p.pending}
                    />
                </div>
            );
    }
}

/**
 * A money figure as a save sends it (ADR-0114 D15): rounded at the currency's places
 * when the user changed it, as it stood when it is the figure the editor opened with.
 */
function atPlacesIfChanged(value: number | null, opened: number | null, currency: string | null): number | null {
    return value === null || value === opened ? value : roundToMinorUnits(value, currency);
}

/**
 * Round to <c>digits</c> decimal places (toFixed → parse). Used by the price ↔ amount
 * link for the figures that are not money, per-share prices and share counts, at six
 * places. The amount the link derives is money and rounds through roundToMinorUnits at
 * the brokerage currency's places (ADR-0114 D15).
 */
function roundTo(value: number, digits: number): number {
    return Number.parseFloat(value.toFixed(digits));
}
