import type {
    CreateInvestmentTransactionRequest,
    LedgerInvestmentAction,
    PatchInvestmentTransactionRequest,
} from '@/lib/types';
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
    feeAccountId: string | null;
    feeAmount: number | null;
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
    | 'feeAmount';

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
 */
export function draftToCreateRequest(
    draft: InvestmentTxnDraft,
): CreateInvestmentTransactionRequest {
    if (!draft.action || !draft.brokerageAccountId) {
        throw new Error('draftToCreateRequest called with incomplete draft.');
    }
    return {
        brokerageAccountId: draft.brokerageAccountId,
        postedAt: new Date(draft.postedAt).toISOString(),
        action: draft.action,
        payee: draft.payee || null,
        memo: draft.memo || null,
        checkNumber: draft.checkNumber || null,
        securityId: draft.securityId,
        shares: draft.shares,
        price: draft.price,
        amount: draft.amount,
        categoryAccountId: draft.categoryAccountId,
        transferAccountId: draft.transferAccountId,
        feeAccountId: draft.feeAccountId,
        feeAmount: draft.feeAmount,
    };
}

/**
 * Convert a validated draft to a PATCH request body (ADR-0029). Same
 * shape as the create request — per ADR-0025 the PATCH-shape's nulls
 * mean "this field is null in the new state," NOT "leave alone." The
 * supplied draft IS the new state of the world.
 */
export function draftToPatchRequest(
    draft: InvestmentTxnDraft,
): PatchInvestmentTransactionRequest {
    if (!draft.action || !draft.brokerageAccountId) {
        throw new Error('draftToPatchRequest called with incomplete draft.');
    }
    return {
        brokerageAccountId: draft.brokerageAccountId,
        postedAt: new Date(draft.postedAt).toISOString(),
        action: draft.action,
        payee: draft.payee || null,
        memo: draft.memo || null,
        checkNumber: draft.checkNumber || null,
        securityId: draft.securityId,
        shares: draft.shares,
        price: draft.price,
        amount: draft.amount,
        categoryAccountId: draft.categoryAccountId,
        transferAccountId: draft.transferAccountId,
        feeAccountId: draft.feeAccountId,
        feeAmount: draft.feeAmount,
    };
}
