import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import { bankRowStrategy } from './bankRowStrategy';
import { investmentRowStrategy } from './investmentRowStrategy';
import type { RegisterRowBodyCtx } from '../shell/RegisterRow';
import { NO_NOTES } from '@/lib/annotations/rowNotesFixture';
import type { BankRow, InvestmentRow } from '@/lib/types';

/**
 * ADR-0023 §H in both registers: a negative amount is red, a positive one is
 * default text and never green, and a Scheduled (future-dated) row drops the
 * colour to the row's own muting, its split lines included.
 */

const TODAY = new Date(2026, 9, 10);
const PAST = '2026-10-02T12:00:00Z';
const FUTURE = '2026-10-20T12:00:00Z';

const ctx = <R,>(variant: RegisterRowBodyCtx['variant']): RegisterRowBodyCtx<R> => ({
    variant,
    currency: 'USD',
    today: TODAY,
    isReadOnly: false,
    systemCategoryIds: new Set(),
});

function bankLeg(over: Partial<BankRow>): BankRow {
    return {
        ...NO_NOTES,
        id: 'leg-1',
        headerId: 'h-1',
        kind: 'bank',
        accountId: 'acc-checking',
        payee: 'Payee A',
        amount: -10,
        postedAt: PAST,
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        balanceAfter: 0,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
        origin: 'manual',
        isPending: false,
        externalId: null,
        checkNumber: null,
        counterpartyId: 'cp-1',
        txnGroupId: 'g-1',
        legIndex: 1,
        counterpartyAccountId: 'acc-groceries',
        counterpartyAccountName: 'Groceries',
        counterpartyAccountType: 'category',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: PAST,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: null,
        isMergeWinner: false,
        importSource: null,
        derivedAction: null,
        accountPostingsOnHeader: 2,
        headerTotalPostings: 3,
        ...over,
    };
}

function investmentRow(over: Partial<InvestmentRow>): InvestmentRow {
    return {
        ...NO_NOTES,
        id: 'inv-1',
        headerId: 'h-2',
        kind: 'investment',
        accountId: 'acc-brokerage',
        payee: 'Contribution',
        amount: 100,
        postedAt: PAST,
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        balanceAfter: 0,
        currencyCode: 'USD',
        balanceCurrencyCode: 'USD',
        origin: 'manual',
        isPending: false,
        investmentAction: 'transfer',
        externalId: null,
        checkNumber: null,
        counterpartyId: 'cp-2',
        txnGroupId: null,
        legIndex: 0,
        counterpartyAccountId: 'acc-checking',
        counterpartyAccountName: 'Checking',
        counterpartyAccountType: 'bank',
        clearedAt: null,
        clearedByUserId: null,
        createdAt: PAST,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        securityId: null,
        securityTicker: null,
        securityName: null,
        quantity: null,
        unitPrice: null,
        postingRole: 'transfer',
        ingestActionHint: null,
        ingestSecurityId: null,
        ingestShares: null,
        ingestUnitPrice: null,
        ingestFee: null,
        ingestAmount: null,
        ingestSecurityTickerHint: null,
        categoryAccountId: null,
        categoryAccountName: null,
        categoryAccountType: null,
        transferAccountId: 'acc-checking',
        transferAccountName: 'Checking',
        transferAccountType: 'bank',
        feeAmount: null,
        feeCategoryId: null,
        feeCategoryName: null,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: null,
        isMergeWinner: false,
        importSource: null,
        derivedAction: 'transfer',
        accountPostingsOnHeader: 1,
        headerTotalPostings: 1,
        ...over,
    };
}

function amountClass(text: string): string {
    return screen.getByText(text).className;
}

describe('bank register split lines', () => {
    it('colours a past line by its sign', () => {
        render(<div>{bankRowStrategy.renderBody(bankLeg({ amount: -10 }), ctx<BankRow>('split-leg'))}</div>);
        expect(amountClass('-$10.00')).toContain('text-state-danger');
    });

    it('drops the danger colour on a line of a future-dated split', () => {
        render(<div>{bankRowStrategy.renderBody(bankLeg({ amount: -10, postedAt: FUTURE }), ctx<BankRow>('split-leg'))}</div>);
        const cls = amountClass('-$10.00');
        expect(cls).not.toContain('text-state-danger');
        expect(cls).not.toContain('text-text ');
    });
});

describe('investment register amounts', () => {
    it('shows a debit in red', () => {
        render(<div>{investmentRowStrategy.renderBody(investmentRow({ amount: -250 }), ctx<InvestmentRow>('txn'))}</div>);
        expect(amountClass('-$250.00')).toContain('text-state-danger');
    });

    it('shows a credit in default text, never green', () => {
        render(<div>{investmentRowStrategy.renderBody(investmentRow({ amount: 250 }), ctx<InvestmentRow>('txn'))}</div>);
        const cls = amountClass('$250.00');
        expect(cls).toContain('text-text');
        expect(cls).not.toContain('state-success');
    });

    it('drops the colour on a Scheduled row', () => {
        render(<div>{investmentRowStrategy.renderBody(investmentRow({ amount: -250, postedAt: FUTURE }), ctx<InvestmentRow>('txn'))}</div>);
        const cls = amountClass('-$250.00');
        expect(cls).not.toContain('text-state-danger');
        expect(cls).not.toContain('text-text');
    });
});
