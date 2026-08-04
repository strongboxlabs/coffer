import { describe, expect, it } from 'vitest';
import type { InvestmentRow } from '@/lib/types';
import { hintToDraft } from './hintToDraft';

// Regression: an imported DivReinvest opened with Amount = 0 (and so
// couldn't be Accepted) because the prefill seeded Amount from the
// brokerage cash leg — which nets to ~0 for a reinvestment. The Amount
// for the linked actions is shares × price; the prefill now seeds that.

const BROKERAGE = '11111111-1111-1111-1111-111111111111';
const SECURITY  = '99999999-9999-9999-9999-999999999999';

const header = {
    postedAt: '2026-05-29T00:00:00Z',
    payee: 'MONEY MARKET FUND A',
    memo: 'DIVIDEND REINVESTMENT',
    checkNumber: null,
};

function mkLeg(
    overrides: Partial<InvestmentRow> & { accountId: string; amount: number },
): InvestmentRow {
    return {
        kind: 'investment',
        id: crypto.randomUUID(),
        payee: header.payee,
        memo: header.memo,
        postedAt: header.postedAt,
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        hasOverrides: false,
        balanceAfter: null,
        origin: 'simplefin',
        isPending: false,
        investmentAction: null,
        externalId: null,
        counterpartyId: 'cp',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        checkNumber: null,
        tags: [],
        headerId: 'h',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: header.postedAt,
        legMemo: null,
        headerMemo: null,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: true,
        securityId: null,
        securityTicker: null,
        securityName: null,
        quantity: null,
        unitPrice: null,
        postingRole: null,
        ingestActionHint: null,
        ingestSecurityId: null,
        ingestShares: null,
        ingestUnitPrice: null,
        ingestFee: null,
        ingestSecurityTickerHint: null,
        categoryAccountId: null,
        categoryAccountName: null,
        categoryAccountType: null,
        transferAccountId: null,
        transferAccountName: null,
        transferAccountType: null,
        feeAmount: null,
        feeCategoryId: null,
        feeCategoryName: null,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: 'simplefin',
        isMergeWinner: false,
        importSource: null,
        derivedAction: null,
        accountPostingsOnHeader: 1,
        headerTotalPostings: 1,
        ...overrides,
    };
}

describe('hintToDraft', () => {
    it('seeds DivReinvest Amount from shares × price (cash leg nets to ~0)', () => {
        // A reinvestment is cash-neutral on the brokerage leg.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 0 })];
        const draft = hintToDraft(
            'dividend_reinvest', BROKERAGE, header, legs,
            /* ingestSecurityId */ null,
            /* ingestShares */ 2063.82,
            /* ingestUnitPrice */ 1,
            /* ingestFee */ null,
        );
        expect(draft.shares).toBe(2063.82);
        expect(draft.price).toBe(1);
        // The bug: this was 0 (from the cash leg). Now shares × price.
        expect(draft.amount).toBe(2063.82);
    });

    it('seeds buy Amount = principal (real cash total minus fee)', () => {
        // Cash leg carries principal + fee (-1010 = 1000 principal + 10 fee);
        // Amount is the fee-excluded principal.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: -1010 })];
        const draft = hintToDraft(
            'buy', BROKERAGE, header, legs, SECURITY, 100, 10, 10,
        );
        expect(draft.amount).toBe(1000);
    });

    it('seeds buy Amount from the real settled total, not shares × rounded-price', () => {
        // Regression: OFX gave 4.878 sh @ 29.45 (rounded) but the trade settled
        // at 143.68; shares × price = 143.66. Recomputing from shares × price
        // silently changed the amount on open — the real total (no fee here) is
        // authoritative.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: -143.68 })];
        const draft = hintToDraft(
            'buy', BROKERAGE, header, legs, SECURITY, 4.878, 29.45, null,
        );
        expect(draft.amount).toBe(143.68);
        // Price is derived from the authoritative amount (÷ |shares|, 6dp),
        // superseding the wire's rounded 29.45 so it reconciles to 143.68.
        expect(draft.price).toBe(
            Number.parseFloat((143.68 / 4.878).toFixed(6)));
        expect(draft.price! * 4.878).toBeCloseTo(143.68, 2);
        expect(draft.price).not.toBe(29.45);
    });

    it('seeds sell Amount as gross proceeds (cash net + fee)', () => {
        // A sell nets proceeds − fee into cash (990 = 1000 proceeds − 10 fee);
        // the Amount field is the gross proceeds.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 990 })];
        const draft = hintToDraft(
            'sell', BROKERAGE, header, legs, SECURITY, 100, 10, 10,
        );
        expect(draft.amount).toBe(1000);
    });

    it('falls back to the signed cash leg when shares/price are absent', () => {
        // dividend_cash isn't a linked action — Amount is user-input,
        // seeded from the signed cash leg.
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 42 })];
        const draft = hintToDraft(
            'dividend_cash', BROKERAGE, header, legs, null, null, null, null,
        );
        expect(draft.amount).toBe(42);
    });
});
