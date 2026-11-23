// Adjust-at-post (ADR-0049): seed the live transaction editors from a reminder's
// template legs so the occurrence dialog opens pre-filled + editable. Bank →
// TxnRowEdit new-mode prefill; investment → the InvestmentTxnDraft (via the same
// legsToDraft inverter the register's Edit/Duplicate use).

import type { ReminderDetail, ReminderLegDto, LedgerInvestmentAction } from '@/lib/types';
import type { ReminderEstimate } from '@/lib/types/reminder';
import type { PostingPrefill, TxnRowNewPrefill } from '@/routes/ledgers/TxnRowEdit';
import type { InvestmentTxnDraft } from '@/routes/ledgers/investment-edit/validation';
import { legsToDraft, type InvestmentLegView } from '@/routes/ledgers/investment-edit/legsToDraft';

const INVESTMENT_ACTIONS: readonly LedgerInvestmentAction[] = [
    'buy', 'buyx', 'sell', 'sellx', 'dividend_cash', 'dividend_reinvest', 'divx', 'transfer', 'misc',
];

function asInvestmentAction(action: string | null): LedgerInvestmentAction | null {
    return action !== null && (INVESTMENT_ACTIONS as readonly string[]).includes(action)
        ? (action as LedgerInvestmentAction) : null;
}

/**
 * The memo and tags a template's editor opens with as the transaction's own.
 * With several postings they are the header's. With one, the header is not in
 * play and holds nothing: the value is the editing side's own leg (the source
 * of a bank template, the brokerage of an investment one), which is where a
 * save from that side writes it.
 */
function transactionNotes(
    detail: ReminderDetail, sideAccountId: string,
): { memo: string | null; tags: readonly string[] } {
    const postingCount = new Set(detail.legs.map((l) => l.postingIndex)).size;
    if (postingCount > 1) return { memo: detail.memo, tags: detail.tags };
    const own = detail.legs.find((l) => l.accountId === sideAccountId);
    return { memo: own?.legMemo ?? null, tags: own?.legTags ?? [] };
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
 *
 * The counterpart's own amount rides along as the posting's second amount, so a fixed
 * transfer between currencies opens with both of its amounts in Post or skip… and the
 * series editor alike, and neither is refilled from a rate (ADR-0114 D14). On a mirrored
 * pair it is the source amount negated, and the editor, which shows a second amount only
 * between real accounts in different currencies, neither shows nor sends it.
 *
 * `estimate` is an occurrence's (Post or skip…), never the series editor's, which edits
 * the template. An occurrence with an estimate opens on it, in one currency or two. An
 * estimating transfer between currencies estimates both sides for each
 * occurrence (ADR-0114 D13), and the dialog shows both figures: the source estimate and
 * the other side's at the due date's rate. With no rate for the due date there is no
 * estimate: the second amount opens empty and required, so nothing posts the template's
 * figure for it.
 */
export function reminderBankPrefill(
    detail: ReminderDetail,
    estimate: ReminderEstimate | null = null,
): { sourceAccountId: string; prefill: TxnRowNewPrefill } | null {
    if (detail.sourceAccountId === null) return null;
    const sourceAccountId = detail.sourceAccountId;

    const byIndex = new Map<number, ReminderLegDto[]>();
    for (const l of detail.legs) {
        const arr = byIndex.get(l.postingIndex);
        if (arr) arr.push(l); else byIndex.set(l.postingIndex, [l]);
    }

    const postings: PostingPrefill[] = [...byIndex.entries()]
        .sort((a, b) => a[0] - b[0])
        .flatMap(([, legs]): PostingPrefill[] => {
            const source = legs.find((l) => l.accountId === sourceAccountId);
            const counterpart = legs.find((l) => l.accountId !== sourceAccountId);
            if (!source || !counterpart) return [];
            return [{
                // Read only when the prefill is the stored template being
                // edited; an occurrence is a new transaction.
                legId: source.legId,
                counterpartyAccountId: counterpart.accountId,
                amount: source.amount,            // source-side signed amount
                // The source side's own memo and set, as the editor edits that
                // side. Seeded so a picked tag adds to what the line holds. On
                // a one-line template they are also the transaction's (below),
                // and here they are what a split copies onto this line.
                legMemo: source.legMemo,
                legTags: source.legTags,
                counterpartyAmount: counterpart.amount,
            }];
        });

    // An estimate is single-posting by construction (the server refuses one on a split).
    // Every estimating series opens on its estimate, the figure the dialog's subtitle
    // prints and auto-post writes, same-currency included (ADR-0049 D10, 2026-11-17).
    const only = postings.length === 1 ? postings[0]! : null;
    if (only !== null && estimate !== null) {
        if (estimate.amount !== null) {
            only.amount = estimate.amount;
            if (estimate.counterpartyAmount != null) only.counterpartyAmount = estimate.counterpartyAmount;
        } else if (estimate.unavailableReason === 'rate-unavailable') {
            only.counterpartyAmount = null;
        }
    }

    // Seeded: what the fields start with is what an edit compares against, so
    // a picked tag adds to the stored set instead of replacing it.
    const notes = transactionNotes(detail, sourceAccountId);
    return {
        sourceAccountId,
        prefill: {
            payee: detail.payee,
            memo: notes.memo,
            tags: notes.tags,
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

    // No transactedAt: an occurrence is materialised ON its schedule date, and the
    // template's tax date belongs to the template's own date, not to this
    // occurrence. Omitted deliberately — the draft defaults to blank, which the
    // payload builder sends as the posted date.
    const notes = transactionNotes(detail, brokerageAccountId);
    const draft = legsToDraft(action, brokerageAccountId, {
        postedAt: occurrenceDate,
        payee: detail.payee,
        memo: notes.memo,
        tags: notes.tags,
        checkNumber: detail.checkNumber,
    }, legs);

    return { brokerageAccountId, draft };
}
