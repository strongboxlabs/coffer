import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { InvestmentTxnRowEdit } from './InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from './validation';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { AccountSummary } from '@/lib/types';

/**
 * Merge candidate chips read like the register row each one stands for: the
 * row's action label (a derived one included), payee, ticker, shares and
 * amount in the register's sign and number format, and a cash transfer's
 * other account as its path. Mocked at `fetch`: the candidate payload is the
 * wire contract the server answers with.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BROKERAGE_ID = '00000000-0000-0000-0000-000000000200';
const HOLDINGS_ID = '00000000-0000-0000-0000-000000000201';
const BANKS_ID = '00000000-0000-0000-0000-000000000100';
const CHECKING_ID = '00000000-0000-0000-0000-000000000101';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';

function account(over: Partial<AccountSummary> & { id: string; name: string }): AccountSummary {
    return {
        ledgerId: LEDGER_ID,
        parentId: null,
        accountType: 'bank',
        categoryKind: null,
        currencyCode: 'USD',
        isActive: true,
        isSystem: false,
        feedConnectionId: null,
        needsReviewCount: 0,
        holdingsAccountId: null,
        isTradeCommission: false,
        ...over,
    };
}

const ACCOUNTS = [
    account({ id: BROKERAGE_ID, name: 'Brokerage', accountType: 'investment', holdingsAccountId: HOLDINGS_ID }),
    account({ id: HOLDINGS_ID, name: 'Brokerage Holdings', accountType: 'investment', isSystem: true }),
    account({ id: BANKS_ID, name: 'Banks' }),
    account({ id: CHECKING_ID, name: 'Checking', parentId: BANKS_ID }),
];

const DRAFT: InvestmentTxnDraft = {
    brokerageAccountId: BROKERAGE_ID,
    postedAt: '2026-05-03',
    transactedAt: '',
    action: 'transfer',
    payee: '',
    memo: '',
    checkNumber: '',
    securityId: null,
    shares: null,
    price: null,
    amount: 500,
    categoryAccountId: null,
    transferAccountId: CHECKING_ID,
    feeAccountId: null,
    feeAmount: null,
    tags: [],
};

/** A sale as the register row shows it: positive cash in, shares shown unsigned. */
const SELL = {
    headerId: 'hdr-sell',
    postedAt: '2026-05-02T00:00:00Z',
    dayDelta: -1,
    action: 'sell',
    derivedAction: 'sell',
    payee: 'Broker sale',
    securityTicker: 'ACME',
    shares: -1250,
    unitPrice: 1.2,
    amount: 1500.5,
    counterpartyAccountId: null,
    counterpartyAccountName: null,
    origin: 'manual',
};

/** A purchase: cash out, so the register shows a negative amount. Imported
 *  earlier and accepted, not entered by hand. */
const BUY = {
    headerId: 'hdr-buy',
    postedAt: '2026-05-04T00:00:00Z',
    dayDelta: 1,
    action: 'buy',
    derivedAction: 'buy',
    payee: 'Broker purchase',
    securityTicker: 'ACME',
    shares: 2500,
    unitPrice: 0.1,
    amount: -250,
    counterpartyAccountId: null,
    counterpartyAccountName: null,
    origin: 'online_import',
};

/** A cash transfer landing here: no header action, the register derives Xfr. */
const XFR = {
    headerId: 'hdr-xfr',
    postedAt: '2026-05-03T00:00:00Z',
    dayDelta: 0,
    action: null,
    derivedAction: 'Xfr',
    payee: 'Contribution',
    securityTicker: null,
    shares: null,
    unitPrice: null,
    amount: 500,
    counterpartyAccountId: CHECKING_ID,
    counterpartyAccountName: 'Checking',
    origin: 'manual',
};

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /\/securities$/, [])
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE_ID, accountName: 'Brokerage', currencyCode: 'USD',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/investment-transactions\/[^/]+\/merge-candidates$/, [SELL, BUY, XFR]);
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderEditor() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={queryClient}>
            <InvestmentTxnRowEdit
                ledgerId={LEDGER_ID}
                brokerageAccountId={BROKERAGE_ID}
                accounts={ACCOUNTS}
                isTradeCommission={false}
                cols="1fr"
                onCancel={() => {}}
                mode={{
                    kind: 'edit',
                    headerId: HEADER_ID,
                    initialDraft: DRAFT,
                    needsReview: true,
                    onSaved: vi.fn(),
                }}
            />
        </QueryClientProvider>,
    );
}

/** The chip's visible parts in order, as the register reads them. */
function parts(chip: HTMLElement): string[] {
    return Array.from(chip.children)
        .filter((el) => el.getAttribute('aria-hidden') !== 'true')
        .map((el) => el.textContent ?? '');
}

async function chipFor(date: string): Promise<HTMLElement> {
    return screen.findByTitle(new RegExp(`row of ${date},`));
}

describe('investment merge candidate chips', () => {
    it('says the candidate is the row that survives, before and after it is picked', async () => {
        renderEditor();

        const expected = 'Folds this feed row into your manual row of 2026-05-02, which is kept as you entered it. Click again to cancel.';
        const chip = await chipFor('2026-05-02');
        expect(chip).toHaveAttribute('title', expected);

        fireEvent.click(chip);

        expect(chip).toHaveAttribute('aria-pressed', 'true');
        expect(chip).toHaveAttribute('title', expected);
        // The label stays the fold's, unchanged.
        expect(screen.getByRole('button', { name: 'Fold into selected →' })).toBeInTheDocument();
    });

    it('says an imported candidate is kept as it is, not as the person entered it', async () => {
        renderEditor();

        expect(await chipFor('2026-05-04')).toHaveAttribute(
            'title',
            'Folds this feed row into the imported row of 2026-05-04, which is kept as it is. Click again to cancel.',
        );
    });

    it('reads a sale like its register row: action label, payee, ticker, unsigned grouped shares, signed amount', async () => {
        renderEditor();

        expect(parts(await chipFor('2026-05-02'))).toEqual([
            '2026-05-02', 'Sell', 'Broker sale', 'ACME', '1,250 sh', '$1,500.50',
        ]);
    });

    it('shows a purchase with the register\'s negative amount', async () => {
        renderEditor();

        expect(parts(await chipFor('2026-05-04'))).toEqual([
            '2026-05-04', 'Buy', 'Broker purchase', 'ACME', '2,500 sh', '-$250.00',
        ]);
    });

    it('names a reinvestment by the word the action picker offers for it', async () => {
        stub.on('GET', /\/investment-transactions\/[^/]+\/merge-candidates$/, [
            { ...BUY, headerId: 'hdr-reinvest', action: 'dividend_reinvest', derivedAction: 'dividend_reinvest', payee: 'Fund payout', amount: 0 },
        ]);
        renderEditor();

        expect(parts(await chipFor('2026-05-04'))[1]).toBe('Reinvest');
        const picker = screen.getByRole('combobox', { name: 'Action' });
        expect(within(picker).getAllByRole('option').map((option) => option.textContent)).toEqual([
            'Pick action…', 'Buy', 'BuyXfr', 'Sell', 'SellXfr', 'Dividend', 'Reinvest', 'DividendXfr', 'Xfr', 'Misc', 'Transfer shares',
        ]);
    });

    it('shows a cash transfer\'s derived action and its other account as a path', async () => {
        renderEditor();

        const chip = await chipFor('2026-05-03');
        expect(parts(chip)).toEqual([
            '2026-05-03', 'Xfr', 'Contribution', '→', 'Banks/Checking', '$500.00',
        ]);
        // Still the merge control it was: the tooltip explains the fold.
        expect(chip).toHaveAttribute('aria-pressed', 'false');
        expect(within(chip).queryByText('sh', { exact: false })).not.toBeInTheDocument();
    });
});
