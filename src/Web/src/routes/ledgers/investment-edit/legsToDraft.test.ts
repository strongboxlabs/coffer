import { describe, expect, it } from 'vitest';
import type { InvestmentRow } from '@/lib/types';
import { legsToDraft } from './legsToDraft';

const BROKERAGE = '11111111-1111-1111-1111-111111111111';
const SIBLING   = '22222222-2222-2222-2222-222222222222';
const CASH_CAT  = '33333333-3333-3333-3333-333333333333';
const FEE_CAT   = '44444444-4444-4444-4444-444444444444';
const XFER_DEST = '55555555-5555-5555-5555-555555555555';
const SECURITY  = '99999999-9999-9999-9999-999999999999';

const header = {
    postedAt: '2026-05-15T00:00:00Z',
    payee: 'Brokerage A',
    memo: 'qtr buy',
    checkNumber: null,
};

function mkLeg(
    overrides: Partial<InvestmentRow> & {
        accountId: string;
        amount: number;
    },
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
        origin: 'manual',
        isPending: false,
        investmentAction: 'buy',
        externalId: null,
        checkNumber: null,
        counterpartyId: 'cp',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: null,
        tags: [],
        headerId: 'h',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-05-15T00:00:00Z',
        legMemo: null,
        headerMemo: null,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        securityId: null,
        securityTicker: null,
        securityName: null,
        quantity: null,
        unitPrice: null,
        postingRole: 'security',
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
        providerKey: null,
        isMergeWinner: false,
        importSource: null,
        derivedAction: null,
        accountPostingsOnHeader: 1,
        headerTotalPostings: 1,
        ...overrides,
    };
}

describe('legsToDraft', () => {
    it('recovers a simple Buy (no fee)', () => {
        const legs = [
            // Cash side: brokerage cash paid out
            mkLeg({ accountId: BROKERAGE, amount: -1000, postingRole: 'security' }),
            // Holdings side: 10 shares at $100
            mkLeg({
                accountId: SIBLING,
                amount: 1000,
                postingRole: 'security',
                securityId: SECURITY,
                quantity: 10,
                unitPrice: 100,
            }),
        ];

        const draft = legsToDraft('buy', BROKERAGE, header, legs);

        expect(draft.action).toBe('buy');
        expect(draft.brokerageAccountId).toBe(BROKERAGE);
        expect(draft.securityId).toBe(SECURITY);
        expect(draft.shares).toBe(10);
        expect(draft.price).toBe(100);
        expect(draft.amount).toBe(1000);
        expect(draft.feeAccountId).toBeNull();
        expect(draft.feeAmount).toBeNull();
        expect(draft.categoryAccountId).toBeNull();
        expect(draft.transferAccountId).toBeNull();
        expect(draft.postedAt).toBe('2026-05-15');
        expect(draft.payee).toBe('Brokerage A');
    });

    it('recovers a Buy with a fee posting', () => {
        const legs = [
            mkLeg({ accountId: BROKERAGE, amount: -1000, postingRole: 'security' }),
            mkLeg({
                accountId: SIBLING,
                amount: 1000,
                postingRole: 'security',
                securityId: SECURITY,
                quantity: 10,
                unitPrice: 100,
            }),
            mkLeg({ accountId: BROKERAGE, amount: -7, postingRole: 'fee' }),
            mkLeg({ accountId: FEE_CAT,   amount: 7,  postingRole: 'fee' }),
        ];

        const draft = legsToDraft('buy', BROKERAGE, header, legs);

        expect(draft.feeAccountId).toBe(FEE_CAT);
        expect(draft.feeAmount).toBe(7);
    });

    it('recovers a Dividend cash (income pair, no security pair)', () => {
        const legs = [
            // Cash side: dividend credited to brokerage cash
            mkLeg({ accountId: BROKERAGE, amount: 50, postingRole: 'income' }),
            // Income category side
            mkLeg({
                accountId: CASH_CAT,
                amount: -50,
                postingRole: 'income',
                securityId: SECURITY,
            }),
        ];

        const draft = legsToDraft('dividend_cash', BROKERAGE, header, legs);

        expect(draft.action).toBe('dividend_cash');
        expect(draft.categoryAccountId).toBe(CASH_CAT);
        expect(draft.amount).toBe(50);
        // No security-pair holdings leg → shares + price remain null.
        expect(draft.shares).toBeNull();
        expect(draft.price).toBeNull();
    });

    it('recovers a Transfer (cash <-> cash, no security)', () => {
        const legs = [
            mkLeg({ accountId: BROKERAGE, amount: -250, postingRole: 'transfer' }),
            mkLeg({ accountId: XFER_DEST, amount: 250,  postingRole: 'transfer' }),
        ];

        const draft = legsToDraft('transfer', BROKERAGE, header, legs);

        expect(draft.action).toBe('transfer');
        expect(draft.transferAccountId).toBe(XFER_DEST);
        expect(draft.amount).toBe(-250);
        expect(draft.securityId).toBeNull();
        expect(draft.categoryAccountId).toBeNull();
    });

    it('recovers a DivXfr (income + transfer)', () => {
        const legs = [
            mkLeg({ accountId: BROKERAGE, amount: 100, postingRole: 'income' }),
            mkLeg({
                accountId: CASH_CAT,
                amount: -100,
                postingRole: 'income',
                securityId: SECURITY,
            }),
            mkLeg({ accountId: BROKERAGE, amount: -100, postingRole: 'transfer' }),
            mkLeg({ accountId: XFER_DEST, amount: 100,  postingRole: 'transfer' }),
        ];

        const draft = legsToDraft('divx', BROKERAGE, header, legs);

        expect(draft.categoryAccountId).toBe(CASH_CAT);
        expect(draft.transferAccountId).toBe(XFER_DEST);
        expect(draft.amount).toBe(100);
    });

    it('returns null for header-string fields when missing', () => {
        const legs = [mkLeg({ accountId: BROKERAGE, amount: 0, postingRole: 'security' })];
        const draft = legsToDraft(
            'buy', BROKERAGE,
            { postedAt: '2026-01-01T00:00:00Z', payee: null, memo: null, checkNumber: null },
            legs,
        );
        expect(draft.payee).toBe('');
        expect(draft.memo).toBe('');
        expect(draft.checkNumber).toBe('');
    });
});
