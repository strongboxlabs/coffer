import { useMemo, useState } from 'react';
import type { LedgerInvestmentAction } from '@/lib/types';
import { type InvestmentTxnDraft, validate, type ValidationErrors } from '../validation';

/**
 * Draft + validation state for the investment editor. Lifted into
 * the orchestrator so per-field components stay controlled (value /
 * onChange) with no internal state — easy to test, no sync issues.
 *
 * The hook owns:
 *   - the current draft object,
 *   - per-field setters,
 *   - re-derived validation errors (`useMemo` over draft),
 *   - a `dirty` flag the orchestrator uses to gate the unsaved-
 *     changes confirm prompt on cancel.
 */
export interface InvestmentTxnDraftHook {
    draft: InvestmentTxnDraft;
    errors: ValidationErrors;
    /** True iff any field has been changed since the initial draft. */
    dirty: boolean;
    /** True iff every required field per the action × matrix is
     * filled AND no per-field error is set. The orchestrator uses
     * this to disable the Save button. */
    isValid: boolean;
    setAction:             (next: LedgerInvestmentAction | null) => void;
    setPostedAt:           (next: string) => void;
    setTransactedAt:       (next: string) => void;
    setPayee:              (next: string) => void;
    setMemo:               (next: string) => void;
    setCheckNumber:        (next: string) => void;
    setSecurityId:         (next: string | null) => void;
    setShares:             (next: number | null) => void;
    setPrice:              (next: number | null) => void;
    setAmount:             (next: number | null) => void;
    setCategoryAccountId:  (next: string | null) => void;
    /** Picks the transfer account. Another account drops the second amount (ADR-0114
     *  D14): one that comes back is prefilled afresh, as for a new entry. */
    setTransferAccountId:  (next: string | null) => void;
    /** The second amount's magnitude; `fixed` when typed (the prefill passes false). */
    setTransferAmount:     (next: number | null, fixed: boolean) => void;
    setFeeAccountId:       (next: string | null) => void;
    setFeeAmount:          (next: number | null) => void;
    setTags:               (next: readonly string[]) => void;
    /** Reset to a fresh draft (cancel + start over). */
    reset: () => void;
}

function makeInitial(brokerageAccountId: string): InvestmentTxnDraft {
    return {
        brokerageAccountId,
        postedAt: new Date().toISOString().slice(0, 10),
        transactedAt: '',
        action: null,
        payee: '',
        memo: '',
        checkNumber: '',
        securityId: null,
        shares: null,
        price: null,
        amount: null,
        categoryAccountId: null,
        transferAccountId: null,
        feeAccountId: null,
        feeAmount: null,
        tags: [],
    };
}

export function useInvestmentTxnDraft(args: {
    brokerageAccountId: string;
    /**
     * Optional seed draft. When supplied, the editor starts in EDIT
     * mode prefilled from this value (typically produced by
     * <c>legsToDraft</c> against an existing header). When omitted,
     * starts in NEW mode with a blank draft.
     *
     * Pass a stable reference (e.g. memoize at the call site) — the
     * hook re-initialises whenever this changes.
     */
    initial?: InvestmentTxnDraft;
}): InvestmentTxnDraftHook {
    const initial = useMemo(
        () => args.initial ?? makeInitial(args.brokerageAccountId),
        [args.initial, args.brokerageAccountId],
    );
    const [draft, setDraft] = useState<InvestmentTxnDraft>(initial);

    const errors = useMemo(() => validate(draft), [draft]);
    const isValid = Object.keys(errors).length === 0;
    const dirty = useMemo(() => !shallowEqual(draft, initial), [draft, initial]);

    function update<K extends keyof InvestmentTxnDraft>(key: K, value: InvestmentTxnDraft[K]) {
        setDraft((prev) => ({ ...prev, [key]: value }));
    }

    return {
        draft,
        errors,
        dirty,
        isValid,
        setAction:             (v) => update('action', v),
        setPostedAt:           (v) => update('postedAt', v),
        setTransactedAt:       (v) => update('transactedAt', v),
        setPayee:              (v) => update('payee', v),
        setMemo:               (v) => update('memo', v),
        setCheckNumber:        (v) => update('checkNumber', v),
        setSecurityId:         (v) => update('securityId', v),
        setShares:             (v) => update('shares', v),
        setPrice:              (v) => update('price', v),
        setAmount:             (v) => update('amount', v),
        setCategoryAccountId:  (v) => update('categoryAccountId', v),
        setTransferAccountId:  (v) => setDraft((prev) => {
            if (prev.transferAccountId === v) return prev;
            const { transferAmount: _a, transferAmountFixed: _f, ...rest } = prev;
            return { ...rest, transferAccountId: v };
        }),
        setTransferAmount:     (v, fixed) => setDraft((prev) => ({
            ...prev, transferAmount: v, transferAmountFixed: fixed,
        })),
        setFeeAccountId:       (v) => update('feeAccountId', v),
        setFeeAmount:          (v) => update('feeAmount', v),
        setTags:               (v) => update('tags', v),
        reset:                 () => setDraft(initial),
    };
}

function shallowEqual<T extends object>(a: T, b: T): boolean {
    const ak = Object.keys(a) as (keyof T)[];
    const bk = Object.keys(b) as (keyof T)[];
    if (ak.length !== bk.length) return false;
    for (const k of ak) {
        const av = a[k];
        const bv = b[k];
        // `tags` is the one array-valued field. Identity would make the draft
        // dirty the moment TagSelect hands back a new array with the same
        // contents — which it does when a tag is checked and then unchecked
        // again — and the unsaved-changes prompt would fire on cancel from an
        // untouched row.
        if (Array.isArray(av) && Array.isArray(bv)) {
            if (av.length !== bv.length) return false;
            if (av.some((x, i) => x !== bv[i])) return false;
            continue;
        }
        if (av !== bv) return false;
    }
    return true;
}
