import type { LedgerInvestmentAction } from '@/lib/types';
import { toDateInputValue } from '@/lib/dates';
import type { InvestmentTxnDraft } from './validation';

/** The minimal leg shape `legsToDraft` actually reads. The register's
 *  `InvestmentRow` satisfies it structurally, and so do reminder-template legs
 *  (ADR-0049) — so this inverter is reusable for both with no cast / no filler. */
export interface InvestmentLegView {
    accountId: string;
    amount: number;
    securityId: string | null;
    quantity: number | null;
    unitPrice: number | null;
    postingRole: 'security' | 'income' | 'transfer' | 'fee' | null;
    /** posting_index (mig 045). Only `transfer_shares` reconstruction
     *  needs it — to pair each brokerage cash leg with its holdings leg
     *  and so map a holdings sibling back to its brokerage. Optional so
     *  non-register callers (reminder templates) needn't supply it. */
    legIndex?: number;
}

/**
 * Inverse of the editor's draft-to-postings build: given the legs of
 * an existing investment header (with their `posting_role` markers
 * from migration 057), reconstruct a draft the editor can render.
 *
 * Per ADR-0019, both legs of a posting share the same `posting_role`.
 * To find the "other side" of each pair (category / transfer / fee
 * destination), filter to legs whose `account_id != brokerageAccountId`.
 *
 * Per ADR-0028, the holdings-side of a security pair carries
 * `security_id`, `quantity`, and `unit_price`. The cash side carries
 * only the cash amount. The inverter pulls shares + price from the
 * holdings-side leg (the one with `quantity != null`).
 *
 * For actions where the user-facing Amount field is computed
 * (Buy / Sell / BuyXfr / SellXfr / DivReinvest / DivXfr), the
 * inverter still sets `amount` from the absolute value of the cash
 * leg — keeps the displayed value consistent on initial render; the
 * orchestrator's per-field components recompute as the user edits.
 *
 * For actions where Amount is user-editable (Dividend cash, Transfer,
 * Misc), `amount` comes from the cash side of the income / transfer /
 * category pair (positive for income, signed for transfer / misc).
 */
export function legsToDraft(
    action: LedgerInvestmentAction,
    brokerageAccountId: string,
    header: {
        postedAt: string;
        payee: string | null;
        memo: string | null;
        checkNumber: string | null;
    },
    legs: readonly InvestmentLegView[],
): InvestmentTxnDraft {
    // transfer_shares (ADR-0065) is holdings → holdings: there's no single
    // brokerage cash leg to anchor on, and the row may be opened from EITHER
    // brokerage's register. Reconstruct it on its own path so the draft is
    // always oriented source → destination (never inverted).
    if (action === 'transfer_shares') {
        return transferSharesToDraft(brokerageAccountId, header, legs);
    }

    const byRole = {
        security: legs.filter((l) => l.postingRole === 'security'),
        income:   legs.filter((l) => l.postingRole === 'income'),
        transfer: legs.filter((l) => l.postingRole === 'transfer'),
        fee:      legs.filter((l) => l.postingRole === 'fee'),
    };

    // Holdings-side of a security pair carries security_id + quantity.
    const securityHoldingsLeg = byRole.security.find(
        (l) => l.securityId !== null && l.quantity !== null,
    ) ?? null;
    // Cash side of any pair = the leg whose account_id matches the
    // brokerage account (the user-visible investment account, NOT the
    // Holdings sibling). Used to pull the cash amount for actions
    // whose Amount field the user edits directly.
    const securityCashLeg = byRole.security.find(
        (l) => l.accountId === brokerageAccountId,
    ) ?? null;

    const incomeCategoryLeg = byRole.income.find(
        (l) => l.accountId !== brokerageAccountId,
    ) ?? null;
    const incomeCashLeg = byRole.income.find(
        (l) => l.accountId === brokerageAccountId,
    ) ?? null;

    const transferDestLeg = byRole.transfer.find(
        (l) => l.accountId !== brokerageAccountId,
    ) ?? null;
    const transferCashLeg = byRole.transfer.find(
        (l) => l.accountId === brokerageAccountId,
    ) ?? null;

    const feeCategoryLeg = byRole.fee.find(
        (l) => l.accountId !== brokerageAccountId,
    ) ?? null;

    return {
        brokerageAccountId,
        postedAt: toDateInputValue(header.postedAt),
        action,
        payee: header.payee ?? '',
        memo: header.memo ?? '',
        checkNumber: header.checkNumber ?? '',
        securityId: securityHoldingsLeg?.securityId ?? null,
        shares: securityHoldingsLeg?.quantity ?? null,
        price: securityHoldingsLeg?.unitPrice ?? null,
        amount: pickAmount(action, {
            securityCashLeg,
            incomeCashLeg,
            transferCashLeg,
        }),
        categoryAccountId: incomeCategoryLeg?.accountId ?? null,
        transferAccountId: transferDestLeg?.accountId ?? null,
        feeAccountId: feeCategoryLeg?.accountId ?? null,
        feeAmount: feeCategoryLeg !== null
            ? Math.abs(feeCategoryLeg.amount)
            : null,
    };
}

/**
 * Reconstruct a `transfer_shares` draft (ADR-0065). The header's legs are
 * per-lot sec pairs: each posting is (brokerage cash $0, holdings ±lot). Pair
 * each cash leg with its holdings leg by `legIndex` so a holdings sibling maps
 * back to its brokerage. The source side carries the negative-quantity holdings
 * legs, the destination the positive ones. The draft is always oriented
 * source → destination, independent of which register the row was opened from,
 * so a subsequent PATCH never inverts the move.
 */
function transferSharesToDraft(
    viewingBrokerageId: string,
    header: {
        postedAt: string;
        payee: string | null;
        memo: string | null;
        checkNumber: string | null;
    },
    legs: readonly InvestmentLegView[],
): InvestmentTxnDraft {
    const byIndex = new Map<number, InvestmentLegView[]>();
    for (const l of legs) {
        const key = l.legIndex ?? -1;
        const arr = byIndex.get(key);
        if (arr) arr.push(l);
        else byIndex.set(key, [l]);
    }

    let sourceBrokerage: string | null = null;
    let destBrokerage: string | null = null;
    let securityId: string | null = null;
    let sharesMoved = 0;

    for (const pair of byIndex.values()) {
        const holdings = pair.find((l) => l.quantity !== null);
        const cash = pair.find((l) => l.quantity === null);
        if (!holdings || !cash || holdings.quantity === null) continue;
        if (holdings.securityId) securityId = holdings.securityId;
        if (holdings.quantity < 0) {
            sourceBrokerage = cash.accountId;
        } else if (holdings.quantity > 0) {
            destBrokerage = cash.accountId;
            sharesMoved += holdings.quantity;
        }
    }

    return {
        brokerageAccountId: sourceBrokerage ?? viewingBrokerageId,
        postedAt: toDateInputValue(header.postedAt),
        action: 'transfer_shares',
        payee: header.payee ?? '',
        memo: header.memo ?? '',
        checkNumber: header.checkNumber ?? '',
        securityId,
        shares: sharesMoved > 0 ? sharesMoved : null,
        price: null,
        amount: null,
        categoryAccountId: null,
        transferAccountId: destBrokerage,
        feeAccountId: null,
        feeAmount: null,
    };
}

function pickAmount(
    action: LedgerInvestmentAction,
    cash: {
        securityCashLeg: InvestmentLegView | null;
        incomeCashLeg: InvestmentLegView | null;
        transferCashLeg: InvestmentLegView | null;
    },
): number | null {
    switch (action) {
        case 'buy':
        case 'buyx':
        case 'sell':
        case 'sellx':
        case 'dividend_reinvest':
            // Computed in the editor from shares × price; display
            // the cash leg's value (absolute) on initial render so
            // the field isn't empty before the user touches anything.
            return cash.securityCashLeg !== null
                ? Math.abs(cash.securityCashLeg.amount)
                : null;
        case 'dividend_cash':
            // Income leg cash side carries the dividend amount as a
            // positive credit to the brokerage cash.
            return cash.incomeCashLeg?.amount ?? null;
        case 'divx':
            // Transfer carries the outgoing cash; user sees the
            // dividend amount, which equals the transfer magnitude.
            return cash.transferCashLeg !== null
                ? Math.abs(cash.transferCashLeg.amount)
                : null;
        case 'transfer':
            return cash.transferCashLeg?.amount ?? null;
        case 'misc':
            // Misc reuses the 'income' role (per importer's MapMisc
            // collapse); sign on cash side discriminates inc vs exp.
            return cash.incomeCashLeg?.amount ?? null;
        case 'transfer_shares':
            // In-kind: no cash amount (handled on its own draft path).
            return null;
    }
}

