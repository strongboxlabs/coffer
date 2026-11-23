import type {
    CreateInvestmentTransactionRequest,
    LedgerInvestmentAction,
    PatchInvestmentTransactionRequest,
} from '@/lib/types';
import { normalizeMemo, sameTagSet } from '@/lib/annotations/notes';
import { ACTION_LAYOUTS, SHARES_SIGN_RULE, type FieldKey } from './actionLayout';

/**
 * Editor draft — what the form holds in flight before save. All
 * fields are optional / nullable here even when ADR-0029's matrix
 * requires them, because the user types them in over time. The
 * validation function below mirrors the server's matrix-driven
 * required-field check, so the client can disable save + show
 * per-field errors before the round-trip.
 */
export interface InvestmentTxnDraft {
    brokerageAccountId: string | null;
    postedAt: string;                   // ISO-8601 date input (yyyy-mm-dd)
    /**
     * Tax / transaction date as a date-input value. Empty string means "no
     * distinct tax date" — the payload builders send `postedAt` for it, never
     * null. Same rule the bank editor follows, so there is ONE rule to remember
     * across both editors.
     */
    transactedAt: string;
    action: LedgerInvestmentAction | null;
    payee: string;
    memo: string;
    checkNumber: string;
    securityId: string | null;
    shares: number | null;
    price: number | null;
    amount: number | null;
    categoryAccountId: string | null;
    transferAccountId: string | null;
    /**
     * The second amount's magnitude: the transfer account's own figure on an Xfr, BuyXfr,
     * SellXfr or DividendXfr to a real account in another currency (ADR-0114 D13, D14),
     * paired with the trade amount, not the fee. Its sign follows from the action and
     * Amount, so the field holds none and a save applies one. Absent or null while there
     * is none; picking another transfer account drops it.
     */
    transferAmount?: number | null;
    /** True once the second amount is typed in, or seeded from a stored figure: it is
     *  never refilled from a rate then (ADR-0114 D14). Absent: it follows Amount. */
    transferAmountFixed?: boolean;
    feeAccountId: string | null;
    feeAmount: number | null;
    /**
     * The event's tags as this brokerage owns them (ADR-0009). Always an
     * array, never null. A create sends them; an edit sends them only when
     * they changed, and an emptied list clears them.
     *
     * Not in ACTION_LAYOUTS: tags apply to every action because they describe
     * the event, not its shape. Only the tag limit applies (see the editor).
     */
    tags: readonly string[];
}

/**
 * Seeds the draft's tax-date field from a header. Blank when the tax date is the
 * posted day — that is the overwhelmingly common case since migration 189 made
 * `transacted_at` NOT NULL (equal to `posted_at` when there is no distinct tax
 * date), and showing it prefilled on every row would be noise.
 */
export function seedTransactedAt(header: {
    postedAt: string;
    transactedAt?: string | null;
}): string {
    if (!header.transactedAt) return '';
    const tax = header.transactedAt.slice(0, 10);
    return tax === header.postedAt.slice(0, 10) ? '' : tax;
}

/**
 * Per-field validation result. `null` on a key = field is OK. A
 * string = the message to render under the field. The orchestrator's
 * save handler also uses this to disable the Save button (any
 * non-null entry blocks save).
 */
export type ValidationErrors = Partial<Record<ValidationKey, string>>;

/**
 * Keys the validator emits errors for. Maps 1:1 to the field
 * components plus a few synthetic keys (`action`, `brokerageAccount`)
 * that don't have a dedicated field component but still need an
 * error surface (rendered in the form's header strip).
 */
export type ValidationKey =
    | 'action'
    | 'brokerageAccount'
    | FieldKey
    | 'feeAmount'
    // The second amount, which the editor checks itself: whether it shows depends on
    // the transfer account's currency, which the draft alone does not know.
    | 'transferAmount';

/**
 * Pure validation function: given an action and a draft, return the
 * per-field error map. No React deps. The orchestrator calls this on
 * every change and uses the result to:
 *   - decorate each field's error display
 *   - disable Save when any entry is non-null
 *
 * Mirrors the server's action × field matrix from ADR-0029. Tests
 * exercise this directly without rendering.
 */
export function validate(draft: InvestmentTxnDraft): ValidationErrors {
    const errors: ValidationErrors = {};

    if (!draft.action) {
        errors.action = 'Pick an action.';
        return errors;
    }
    if (!draft.brokerageAccountId) {
        errors.brokerageAccount = 'Brokerage account is required.';
    }

    const fields = ACTION_LAYOUTS[draft.action];
    for (const key of fields) {
        const fieldError = validateField(key, draft, draft.action);
        if (fieldError !== null) errors[key] = fieldError;
    }

    // Fee fields have a paired-presence rule that crosses two field
    // boundaries (account ⇔ amount). Surfaced on `feeAmount` so the
    // amount input shows the message under it.
    if (draft.feeAccountId && draft.feeAmount === null) {
        errors.feeAmount = 'Fee amount is required when a fee category is set.';
    } else if (!draft.feeAccountId && draft.feeAmount !== null) {
        errors.feeAmount = 'Pick a fee category, or clear the amount.';
    } else if (draft.feeAmount !== null && draft.feeAmount <= 0) {
        errors.feeAmount = 'Fee amount must be positive.';
    }
    return errors;
}

function validateField(
    key: FieldKey,
    draft: InvestmentTxnDraft,
    action: LedgerInvestmentAction,
): string | null {
    switch (key) {
        case 'security':
            // Required for every action except 'transfer' (which the
            // matrix already excludes); on 'misc' the field is shown
            // but optional, so accept null.
            if (action === 'misc') return null;
            return draft.securityId ? null : 'Security is required.';

        case 'shares': {
            if (draft.shares === null) return 'Shares are required.';
            if (draft.shares === 0) return 'Shares must be non-zero.';
            const sign = SHARES_SIGN_RULE[action];
            if (sign === 'positive' && draft.shares < 0)
                return 'Shares must be positive for this action.';
            if (sign === 'negative' && draft.shares > 0)
                return 'Shares must be negative for this action (sell).';
            return null;
        }

        case 'price':
            if (draft.price === null) return 'Price is required.';
            if (draft.price <= 0) return 'Price must be positive.';
            return null;

        case 'amount':
            // Misc / transfer / dividend_cash: sign is meaningful but
            // any non-zero value is acceptable. Server's matrix only
            // requires presence.
            if (draft.amount === null) return 'Amount is required.';
            if (draft.amount === 0) return 'Amount must be non-zero.';
            return null;

        case 'category':
            return draft.categoryAccountId
                ? null
                : 'Category is required for this action.';

        case 'transfer':
            return draft.transferAccountId
                ? null
                : 'Transfer destination is required for this action.';

        case 'fee':
            // Fee is always optional at the field level; the
            // paired-presence rule above handles account ⇔ amount.
            return null;
    }
}

/**
 * Convert a validated draft to the API request body. Caller should
 * have already confirmed via `validate(draft)` returns no errors
 * (otherwise the server will reject with the same matrix-driven
 * codes — but it's nicer to gate at the client too).
 *
 * `transferAmount` is the second amount as it is sent: the transfer account's own leg,
 * signed, on a transfer between currencies (ADR-0114 D13). Null leaves the key out, as a
 * category or same-currency transfer does, so its body is as before.
 */
export function draftToCreateRequest(
    draft: InvestmentTxnDraft,
    transferAmount: number | null = null,
): CreateInvestmentTransactionRequest {
    if (!draft.action || !draft.brokerageAccountId) {
        throw new Error('draftToCreateRequest called with incomplete draft.');
    }
    return {
        brokerageAccountId: draft.brokerageAccountId,
        postedAt: new Date(draft.postedAt).toISOString(),
        // Blank tax date sends the posted date. The investment PATCH is
        // wholesale-replace, so OMITTING this field is not "leave it alone" — it
        // clears the stored tax date. Always sending a value is what keeps an
        // unrelated edit from destroying a tax date the user never touched.
        transactedAt: new Date(draft.transactedAt || draft.postedAt).toISOString(),
        action: draft.action,
        payee: draft.payee || null,
        memo: normalizeMemo(draft.memo),
        checkNumber: draft.checkNumber || null,
        securityId: draft.securityId,
        shares: draft.shares,
        price: draft.price,
        amount: draft.amount,
        categoryAccountId: draft.categoryAccountId,
        transferAccountId: draft.transferAccountId,
        ...(transferAmount !== null ? { transferAmount } : {}),
        feeAccountId: draft.feeAccountId,
        feeAmount: draft.feeAmount,
        // Always sent, never omitted. The editor holds the whole tag set, so an
        // emptied list has to reach the server as [] — omitting it is the
        // server's "leave them alone", which would make a tag impossible to
        // remove from this surface.
        tags: draft.tags,
    };
}

/** The memo and tags that differ from what the editor opened with; the rest are left out. */
function changedNotes(
    draft: Pick<InvestmentTxnDraft, 'memo' | 'tags'>,
    opened: { memo: string; tags: readonly string[] },
): { memo?: string | null; tags?: readonly string[] } {
    const memo = normalizeMemo(draft.memo);
    const notes: { memo?: string | null; tags?: readonly string[] } = {};
    if (memo !== normalizeMemo(opened.memo)) notes.memo = memo;
    if (!sameTagSet(draft.tags, opened.tags)) notes.tags = draft.tags;
    return notes;
}

/**
 * A stored template's create-shaped body (an investment reminder edit), with
 * memo and tags sent only when they differ from what the editor opened with.
 * The body replaces the template's shape, but its memo and tags follow
 * presence like the PATCH: an omitted memo or tag set is left as it is, so an
 * edit never clears what the editor could not show.
 */
export function draftToStoredRequest(
    draft: InvestmentTxnDraft,
    opened: { memo: string; tags: readonly string[] },
    transferAmount: number | null = null,
): CreateInvestmentTransactionRequest {
    const { memo: _memo, tags: _tags, ...rest } = draftToCreateRequest(draft, transferAmount);
    return { ...rest, ...changedNotes(draft, opened) };
}

/**
 * Convert a validated draft to a PATCH request body (ADR-0029). Same
 * shape as the create request — per ADR-0025 the PATCH-shape's nulls
 * mean "this field is null in the new state," NOT "leave alone." The
 * supplied draft IS the new state of the world.
 *
 * Except memo and tags, which follow PRESENCE: sent only when they differ from
 * what the editor opened with (`opened`). An omitted memo is left as it is, so
 * an untouched save from a brokerage that shows the other side's memo writes
 * nothing, and never copies that memo onto the brokerage as its own.
 */
export function draftToPatchRequest(
    draft: InvestmentTxnDraft,
    opened: { memo: string; tags: readonly string[] },
    transferAmount: number | null = null,
): PatchInvestmentTransactionRequest {
    if (!draft.action || !draft.brokerageAccountId) {
        throw new Error('draftToPatchRequest called with incomplete draft.');
    }
    const notes = changedNotes(draft, opened);
    return {
        brokerageAccountId: draft.brokerageAccountId,
        postedAt: new Date(draft.postedAt).toISOString(),
        // Blank tax date sends the posted date. The investment PATCH is
        // wholesale-replace, so OMITTING this field is not "leave it alone" — it
        // clears the stored tax date. Always sending a value is what keeps an
        // unrelated edit from destroying a tax date the user never touched.
        transactedAt: new Date(draft.transactedAt || draft.postedAt).toISOString(),
        action: draft.action,
        payee: draft.payee || null,
        checkNumber: draft.checkNumber || null,
        securityId: draft.securityId,
        shares: draft.shares,
        price: draft.price,
        amount: draft.amount,
        categoryAccountId: draft.categoryAccountId,
        transferAccountId: draft.transferAccountId,
        ...(transferAmount !== null ? { transferAmount } : {}),
        feeAccountId: draft.feeAccountId,
        feeAmount: draft.feeAmount,
        ...notes,
    };
}
