// Adjust-at-post (ADR-0049): seed the live transaction editors from a reminder's
// template legs so the occurrence dialog opens pre-filled + editable. Bank →
// TxnRowEdit new-mode prefill; investment → the InvestmentTxnDraft (via the same
// legsToDraft inverter the register's Edit/Duplicate use).

import type { ReminderDetail, ReminderLegDto, LedgerInvestmentAction } from '@/lib/types';
import type { TxnRowNewPrefill } from '@/routes/ledgers/TxnRowEdit';
import type { InvestmentTxnDraft } from '@/routes/ledgers/investment-edit/validation';
import { legsToDraft, type InvestmentLegView } from '@/routes/ledgers/investment-edit/legsToDraft';

const INVESTMENT_ACTIONS: readonly LedgerInvestmentAction[] = [
    'buy', 'buyx', 'sell', 'sellx', 'dividend_cash', 'dividend_reinvest', 'divx', 'transfer', 'misc',
];

function asInvestmentAction(action: string | null): LedgerInvestmentAction | null {
    return action !== null && (INVESTMENT_ACTIONS as readonly string[]).includes(action)
        ? (action as LedgerInvestmentAction) : null;
}

const POSTING_ROLES = ['security', 'income', 'transfer', 'fee'] as const;
function asPostingRole(role: string | null): InvestmentLegView['postingRole'] {
    return role !== null && (POSTING_ROLES as readonly string[]).includes(role)
        ? (role as InvestmentLegView['postingRole']) : null;
}

/**
 * Build the bank editor's new-mode prefill from a bank reminder's template legs.
 * Each posting = the source-side leg (on `sourceAccountId`) paired with its
 * counterpart (same `postingIndex`); the posting amount is the source-side
 * signed amount, so single rows AND splits round-trip. Null when the series has
 * no source account (custom / pre-125).
 */
export function reminderBankPrefill(
    detail: ReminderDetail,
): { sourceAccountId: string; prefill: TxnRowNewPrefill } | null {
    if (detail.sourceAccountId === null) return null;
    const sourceAccountId = detail.sourceAccountId;

    const byIndex = new Map<number, ReminderLegDto[]>();
    for (const l of detail.legs) {
        const arr = byIndex.get(l.postingIndex);
        if (arr) arr.push(l); else byIndex.set(l.postingIndex, [l]);
    }

    const postings = [...byIndex.entries()]
        .sort((a, b) => a[0] - b[0])
        .flatMap(([, legs]) => {
            const source = legs.find((l) => l.accountId === sourceAccountId);
            const counterpart = legs.find((l) => l.accountId !== sourceAccountId);
            if (!source || !counterpart) return [];
            return [{
                counterpartyAccountId: counterpart.accountId,
                amount: source.amount,            // source-side signed amount
                legMemo: source.legMemo,
            }];
        });

    return {
        sourceAccountId,
        prefill: {
            payee: detail.payee,
            memo: detail.memo,
            checkNumber: detail.checkNumber,
            postings,
        },
    };
}

/**
 * Build the investment editor's draft from an investment reminder's template
 * legs, reusing `legsToDraft` (the inverter the register's Edit/Duplicate use).
 * The brokerage = the series' source account (mig 125); the occurrence date
 * seeds the posted date. Null when the series lacks a source account or a
 * recognized investment action.
 */
export function reminderInvestmentDraft(
    detail: ReminderDetail, occurrenceDate: string,
): { brokerageAccountId: string; draft: InvestmentTxnDraft } | null {
    const action = asInvestmentAction(detail.action);
    if (detail.sourceAccountId === null || action === null) return null;
    const brokerageAccountId = detail.sourceAccountId;

    const legs: InvestmentLegView[] = detail.legs.map((l) => ({
        accountId: l.accountId,
        amount: l.amount,
        securityId: l.securityId,
        quantity: l.quantity,
        unitPrice: l.unitPrice,
        postingRole: asPostingRole(l.postingRole),
    }));

    const draft = legsToDraft(action, brokerageAccountId, {
        postedAt: occurrenceDate,
        payee: detail.payee,
        memo: detail.memo,
        checkNumber: detail.checkNumber,
    }, legs);

    return { brokerageAccountId, draft };
}
