import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { InvestmentTxnRowEdit } from './InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from './validation';
import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { currencyAffix, formatCurrency, formatSignedAmount } from '@/lib/money';
import type { AccountSummary } from '@/lib/types';

/**
 * Every figure in the investment editor is in the brokerage's currency: the
 * host's when its data carries one (a reminder occurrence's, a home entry's),
 * else the account list's. A list without the brokerage (a deactivated one left
 * out) says nothing, so with no host currency either the editor prints no
 * figure: a placeholder for the amount, no merge candidates. Mocked at `fetch`.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const BROKERAGE_ID = '00000000-0000-0000-0000-000000000200';
const CHECKING_ID = '00000000-0000-0000-0000-000000000101';
const HEADER_ID = '00000000-0000-0000-0000-000000000aaa';

/** The list the host hands over, without the brokerage. */
const ACCOUNTS: AccountSummary[] = [{
    id: CHECKING_ID, name: 'Account A', ledgerId: LEDGER_ID, parentId: null, accountType: 'bank',
    categoryKind: null, currencyCode: 'EUR', isActive: true, isSystem: false, feedConnectionId: null,
    needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
}];

/** A feed row with no action picked yet: the editor shows its amount read-only. */
const DRAFT: InvestmentTxnDraft = {
    brokerageAccountId: BROKERAGE_ID, postedAt: '2026-05-03', transactedAt: '', action: null,
    payee: '', memo: '', checkNumber: '', securityId: null, shares: null, price: null, amount: -420,
    categoryAccountId: null, transferAccountId: null, feeAccountId: null, feeAmount: null, tags: [],
};

const CANDIDATE = {
    headerId: 'hdr-buy', postedAt: '2026-05-02T00:00:00Z', dayDelta: -1, action: 'buy',
    derivedAction: 'buy', payee: 'Payee B', securityTicker: 'SECA', shares: 10, unitPrice: 42,
    amount: -420, counterpartyAccountId: null, counterpartyAccountName: null, origin: 'manual',
};

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /\/securities$/, [])
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE_ID, accountName: 'Brokerage A', currencyCode: 'EUR',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/investment-transactions\/[^/]+\/merge-candidates$/, [CANDIDATE]);
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function editor(currency: string | undefined) {
    return (
        <InvestmentTxnRowEdit
            ledgerId={LEDGER_ID}
            brokerageAccountId={BROKERAGE_ID}
            accounts={ACCOUNTS}
            currency={currency}
            isTradeCommission={false}
            cols="1fr"
            onCancel={() => {}}
            mode={{ kind: 'edit', headerId: HEADER_ID, initialDraft: DRAFT, needsReview: true, onSaved: vi.fn() }}
        />
    );
}

function renderEditor(currency: string | undefined) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrap = (ui: JSX.Element) => <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>;
    const view = render(wrap(editor(currency)));
    return { rerender: (next: string | undefined) => view.rerender(wrap(editor(next))) };
}

/** The read-only amount line shown until an action is picked. */
const amountLine = () => screen.getByText('— pick an action to see its fields.').parentElement as HTMLElement;

describe('the investment editor\'s currency', () => {
    it('is the host\'s when the account list does not hold the brokerage', async () => {
        renderEditor('EUR');

        expect(await screen.findByTitle(/row of 2026-05-02,/)).toHaveTextContent(formatSignedAmount(-420, 'EUR'));
        expect(amountLine()).toHaveTextContent(formatCurrency(-420, 'EUR'));
        expect(screen.queryByText(/\$/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('prints no figure while neither the host nor the list knows it, and prints them once the host does', async () => {
        const { rerender } = renderEditor(undefined);

        // The candidates have been read and the page has settled on them.
        await waitFor(() => expect(stub.callsTo('GET', /\/merge-candidates$/)).toHaveLength(1));
        await new Promise((resolve) => setTimeout(resolve, 50));
        expect(amountLine().querySelector('[aria-hidden]')).toHaveClass('motion-safe:animate-pulse');
        expect(screen.queryByTitle(/row of 2026-05-02,/)).toBeNull();
        expect(screen.queryByText(/[$€]/)).toBeNull();

        rerender('EUR');

        expect(await screen.findByTitle(/row of 2026-05-02,/)).toHaveTextContent(formatSignedAmount(-420, 'EUR'));
        expect(amountLine()).toHaveTextContent(formatCurrency(-420, 'EUR'));
        expect(screen.queryByText(/\$/)).toBeNull();
    });
});

// ADR-0114 D14, D15: Amount and Fee are money at the brokerage currency's places, its
// symbol inside the field; Price shows the same symbol but is not money, so it takes
// any step and is never rounded; with no currency known, no symbol and no rounding.
describe('the investment editor\'s inputs at the brokerage currency\'s places', () => {
    const SECURITY_ID = '00000000-0000-0000-0000-000000000900';
    const brokerage = (currencyCode: string): AccountSummary => ({
        ...ACCOUNTS[0]!, id: BROKERAGE_ID, name: 'Brokerage A', accountType: 'investment', currencyCode,
    });
    const BUY: InvestmentTxnDraft = { ...DRAFT, action: 'buy', amount: null };

    function renderBuy(accounts: AccountSummary[], draft: InvestmentTxnDraft = BUY) {
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
            <QueryClientProvider client={queryClient}>
                <InvestmentTxnRowEdit
                    ledgerId={LEDGER_ID}
                    brokerageAccountId={BROKERAGE_ID}
                    accounts={accounts}
                    isTradeCommission={false}
                    cols="1fr"
                    onCancel={() => {}}
                    mode={{ kind: 'edit', headerId: HEADER_ID, initialDraft: draft, needsReview: false, onSaved: vi.fn() }}
                />
            </QueryClientProvider>,
        );
    }
    const field = (name: string) => screen.getByLabelText(name) as HTMLInputElement;
    const affixOf = (input: HTMLElement) =>
        input.parentElement!.querySelector('[data-money-affix]')?.textContent ?? null;
    const typeAndLeave = (input: HTMLElement, text: string) => {
        fireEvent.focus(input);
        fireEvent.change(input, { target: { value: text } });
        fireEvent.blur(input);
    };

    it('derives a whole-unit Amount from shares and price, a midpoint away from zero', async () => {
        renderBuy([...ACCOUNTS, brokerage('JPY')]);

        await screen.findByLabelText('Amount');
        typeAndLeave(field('Shares'), '3');
        typeAndLeave(field('Price'), '333.5');
        expect(field('Amount').value).toBe('1001');
        expect(field('Amount')).toHaveAttribute('step', '1');
        expect(affixOf(field('Amount'))).toBe(currencyAffix('JPY')!.symbol);
        expect(affixOf(field('Fee amount'))).toBe(currencyAffix('JPY')!.symbol);
    });

    it('derives a three-place Amount on a three-place brokerage', async () => {
        renderBuy([...ACCOUNTS, brokerage('KWD')]);

        await screen.findByLabelText('Amount');
        typeAndLeave(field('Shares'), '2');
        typeAndLeave(field('Price'), '1.2345');
        expect(field('Amount').value).toBe('2.469');
    });

    it('shows the brokerage\'s symbol on Price, takes any step and keeps six places', async () => {
        // The security is priced in another currency; the trade's amount is not.
        stub.on('GET', /\/securities$/, [{
            id: SECURITY_ID, ticker: 'SECA', cusip: null, name: 'Security A', assetClass: null, exchange: null,
            isActive: true, totalQuantity: 0, latestPrice: null, latestPriceAsOf: null, priceCurrencyCode: 'USD',
        }]);
        renderBuy([...ACCOUNTS, brokerage('JPY')], { ...BUY, securityId: SECURITY_ID });

        const price = await screen.findByLabelText('Price') as HTMLInputElement;
        expect(affixOf(price)).toBe(currencyAffix('JPY')!.symbol);
        expect(price).toHaveAttribute('step', 'any');
        typeAndLeave(price, '12.345678');
        expect(price.value).toBe('12.345678');
    });

    it('shows no symbol and rounds nothing while the brokerage\'s currency is not known', async () => {
        // The list does not hold the brokerage and the host names no currency.
        renderBuy(ACCOUNTS);

        const amount = await screen.findByLabelText('Amount') as HTMLInputElement;
        expect(document.querySelector('[data-money-affix]')).toBeNull();
        typeAndLeave(amount, '12.345');
        expect(amount.value).toBe('12.345');
    });

    it('rounds an Amount changed with no blur after it in the save body', async () => {
        stub
            .on('GET', /\/securities$/, [{
                id: SECURITY_ID, ticker: 'SECA', cusip: null, name: 'Security A', assetClass: null, exchange: null,
                isActive: true, totalQuantity: 0, latestPrice: null, latestPriceAsOf: null, priceCurrencyCode: 'JPY',
            }])
            .on('PATCH', /\/investment-transactions\/[^/]+$/, []);
        renderBuy([...ACCOUNTS, brokerage('JPY')], {
            ...BUY, securityId: SECURITY_ID, shares: 2, price: 500, amount: 1000,
        });

        const amount = await screen.findByLabelText('Amount');
        fireEvent.change(amount, { target: { value: '1000.5' } });
        fireEvent.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/investment-transactions\//)).toHaveLength(1));
        expect((stub.callsTo('PATCH', /\/investment-transactions\//)[0]!.body as { amount: number }).amount).toBe(1001);
    });
});
