import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import { formatLedgerDate } from '@/lib/dates';
import { formatSignedAmount } from '@/lib/money';
import type { AccountSummary, ReminderDetail, UpcomingOccurrence } from '@/lib/types';
import type { FireBankReminderRequest, ReminderEstimate } from '@/lib/types/reminder';

import { ReminderOccurrenceModal } from './ReminderOccurrenceModal';

/**
 * The occurrence dialog prints its figures in the source account's currency, and
 * none in a currency it does not know yet: the occurrence's own code at once, else
 * the account list's once it arrives. Mocked at `fetch`, with the account read held
 * until the test lets it answer.
 */

const LEDGER = 'ledger-1';
const CHECKING = 'acct-bank';
const RENT = 'cat-rent';

const base = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'EUR',
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
    holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: CHECKING, name: 'Account A', accountType: 'bank' },
    { ...base, id: RENT, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
];

const leg = (accountId: string, accountName: string, amount: number) => ({
    legId: `tleg-${accountName}`, accountId, accountName, postingIndex: 0, amount, legMemo: null, legTags: [],
    securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null,
});

const DETAIL: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Payee A', memo: null, tags: [], checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-11-01', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: CHECKING,
    legs: [leg(CHECKING, 'Account A', -900), leg(RENT, 'Category A', 900)],
};

/** An occurrence as a caller without the source's currency hands it over. */
const OCC: UpcomingOccurrence = {
    date: '2026-11-01', kind: 'reminder', reminderId: 'r1', headerId: null,
    payee: 'Payee A', memo: null, amount: -900, seriesNextDue: '2026-11-01',
};

let stub: FetchStub;
let openAccounts: () => void;

beforeEach(() => {
    stub = stubFetch();
    stub
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/reminders\/r1$/, DETAIL)
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, []);
    // The account read waits until the test opens it; every other read answers.
    let open!: () => void;
    const gate = new Promise<void>((resolve) => { open = resolve; });
    openAccounts = open;
    const answer = globalThis.fetch;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        if (new URL(raw, 'http://localhost').pathname.endsWith('/accounts')) await gate;
        return answer(input, init);
    });
});
afterEach(() => {
    vi.unstubAllGlobals();
});

function renderModal(occ: UpcomingOccurrence) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={qc}>
            <ReminderOccurrenceModal ledgerId={LEDGER} occ={occ} onClose={() => {}} onActed={() => {}} />
        </QueryClientProvider>,
    );
}

/** The dialog's subtitle: the date, the amount and the account. */
function subtitle(): HTMLElement {
    return screen.getByText(new RegExp(formatLedgerDate(OCC.date).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
}

describe('the occurrence dialog\'s figures', () => {
    it('print no amount before the account says its currency, then print it in that currency', async () => {
        renderModal(OCC);

        // Anchored on the subtitle's date, which needs nothing loaded, and the
        // body's loading line: the account read has not answered.
        expect(subtitle()).toBeInTheDocument();
        expect(screen.getByText('Loading…')).toBeInTheDocument();
        expect(subtitle().textContent).not.toMatch(/[$€]/);
        expect(stub.callsTo('GET', /\/accounts$/)).toHaveLength(0);

        openAccounts();

        await waitFor(() => expect(subtitle()).toHaveTextContent(formatSignedAmount(-900, 'EUR')));
        expect(screen.queryByText(/\$/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });

    it('print the occurrence\'s own currency at once, without waiting for the account', async () => {
        renderModal({ ...OCC, sourceAccountId: CHECKING, sourceAccountName: 'Account A', currencyCode: 'EUR' });

        expect(screen.getByText('Loading…')).toBeInTheDocument();
        expect(subtitle()).toHaveTextContent(formatSignedAmount(-900, 'EUR'));

        openAccounts();
        await waitFor(() => expect(screen.queryByText('Loading…')).toBeNull());
        expect(screen.queryByText(/\$/)).toBeNull();
        expect(stub.unrouted).toEqual([]);
    });
});

describe('the occurrence dialog\'s "≈"', () => {
    const atOnce = { ...OCC, sourceAccountId: CHECKING, sourceAccountName: 'Account A', currencyCode: 'EUR' };
    const amount = formatSignedAmount(-900, 'EUR');

    async function settled() {
        openAccounts();
        await waitFor(() => expect(screen.queryByText('Loading…')).toBeNull());
        expect(subtitle()).toHaveTextContent(amount);
    }

    it('marks an estimating series\' amount that has an estimate', async () => {
        renderModal({ ...atOnce, estimate: { requestedSampleCount: 3, availableSampleCount: 2, amount: -900, unavailableReason: null } });
        await settled();
        expect(subtitle()).toHaveTextContent(`≈${amount}`);
        expect(screen.getByText('≈')).toHaveAttribute('title', 'Estimated from the last 2 occurrences');
    });

    it('marks an estimating series\' amount before it has one', async () => {
        renderModal({ ...atOnce, estimate: { requestedSampleCount: 3, availableSampleCount: 0, amount: null, unavailableReason: 'no-history' } });
        await settled();
        expect(subtitle()).toHaveTextContent(`≈${amount}`);
        expect(screen.getByText('≈'))
            .toHaveAttribute('title', 'Estimated: the amount entered is used until an estimate can be made');
    });

    it('leaves a fixed amount plain', async () => {
        renderModal(atOnce);
        await settled();
        expect(subtitle().textContent).not.toContain('≈');
    });
});

// ADR-0114 D14: a fixed-amount reminder between currencies seeds both amounts in Post or
// skip…, and neither is refilled from a rate. ADR-0114 D13: an estimating one seeds both
// sides of the occurrence's estimate, and with no rate for the due date its second amount
// is empty and required. Mocked at fetch, with every read answering at once.
describe('Post or skip… on a transfer between currencies', () => {
    const USD_BANK = 'acct-usd';
    const EUR_BANK = 'acct-eur';
    const SAVINGS = 'acct-savings';
    const XCCY_ACCOUNTS: AccountSummary[] = [
        { ...base, id: USD_BANK, name: 'Test USD Bank', accountType: 'bank', currencyCode: 'USD' },
        { ...base, id: EUR_BANK, name: 'Test EUR Bank', accountType: 'bank', currencyCode: 'EUR' },
        { ...base, id: SAVINGS, name: 'Test USD Savings', accountType: 'bank', currencyCode: 'USD' },
    ];
    const template = (counterpartId: string, name: string, counterpartAmount: number): ReminderDetail => ({
        ...DETAIL, sourceAccountId: USD_BANK,
        legs: [leg(USD_BANK, 'Test USD Bank', -100), leg(counterpartId, name, counterpartAmount)],
    });
    const occ = (estimate?: ReminderEstimate): UpcomingOccurrence => ({
        ...OCC, amount: estimate?.amount ?? -100, sourceAccountId: USD_BANK,
        sourceAccountName: 'Test USD Bank', currencyCode: 'USD', ...(estimate ? { estimate } : {}),
    });
    let xccy: FetchStub;

    function answering(detail: ReminderDetail) {
        xccy = stubFetch()
            .on('GET', /\/accounts$/, XCCY_ACCOUNTS)
            .on('GET', /\/payees$/, [])
            .on('GET', /\/tags$/, [])
            .on('GET', /\/reminders\/r1$/, detail)
            .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
            .on('GET', /\/fx-rates\/USD\/EUR\/derived$/, (call: { search: URLSearchParams }) => ({
                fromCurrency: 'USD', toCurrency: 'EUR', date: call.search.get('date'), reportingCurrency: 'USD',
                rate: null, rateDate: null, legs: [], converted: null,
                rateUnavailable: [{ fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-11-01', reason: 'rate_unavailable' }],
            }))
            .on('POST', /\/fire\/bank$/, { headerId: 'h1', skippedEarlierCount: 0, skippedEarlierFrom: null });
    }

    async function posted(): Promise<FireBankReminderRequest> {
        fireEvent.click(await screen.findByRole('button', { name: 'Post' }));
        await waitFor(() => expect(xccy.callsTo('POST', /\/fire\/bank$/)).toHaveLength(1));
        return xccy.callsTo('POST', /\/fire\/bank$/)[0]!.body as FireBankReminderRequest;
    }

    it('seeds both template amounts, asks for no rate, and posts them together', async () => {
        answering(template(EUR_BANK, 'Test EUR Bank', 90));
        renderModal(occ());

        const field = await screen.findByLabelText('Amount received in Test EUR Bank, in EUR');
        expect((field as HTMLInputElement).value).toBe('90.00');
        expect(screen.getByText('1 USD = 0.9000 EUR, from the two amounts')).toBeInTheDocument();
        const body = await posted();
        expect(body.postings).toEqual([{ legId: null, counterpartyAccountId: EUR_BANK, amount: -100, counterpartyAmount: 90 }]);
        expect(xccy.callsTo('GET', /\/derived$/)).toHaveLength(0);
    });

    it('seeds both sides of an estimating series\' occurrence from its estimate', async () => {
        answering(template(EUR_BANK, 'Test EUR Bank', 90));
        renderModal(occ({
            requestedSampleCount: 3, availableSampleCount: 3, amount: -120, unavailableReason: null,
            counterpartyAmount: 108,
        }));

        const field = await screen.findByLabelText('Amount received in Test EUR Bank, in EUR');
        expect((field as HTMLInputElement).value).toBe('108.00');
        expect(screen.getByLabelText('Amount')).toHaveValue(-120);
        const body = await posted();
        expect(body.postings[0]).toMatchObject({ amount: -120, counterpartyAmount: 108 });
    });

    it('leaves the second amount empty and required when the due date has no rate, and holds Post', async () => {
        answering(template(EUR_BANK, 'Test EUR Bank', 90));
        renderModal(occ({
            requestedSampleCount: 3, availableSampleCount: 3, amount: null, unavailableReason: 'rate-unavailable',
            rateUnavailable: [{ fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-11-01', reason: 'rate_unavailable' }],
        }));

        const field = await screen.findByLabelText('Amount received in Test EUR Bank, in EUR');
        expect(field).toHaveValue(null);
        expect(await screen.findByText('No EUR to USD rate for Nov 1, 2026')).toBeInTheDocument();
        const post = screen.getByRole('button', { name: 'Post' });
        expect(post).toBeDisabled();
        expect(post).toHaveAttribute('title', 'Enter the amount in EUR that arrived in Test EUR Bank.');
    });

    it('posts a same-currency template\'s body as before, with no second amount', async () => {
        answering(template(SAVINGS, 'Test USD Savings', 100));
        renderModal(occ());

        // Anchor: the editor is up, with the template's Amount.
        expect(await screen.findByLabelText('Amount')).toHaveValue(-100);
        expect(screen.queryByText('Received')).toBeNull();
        const body = await posted();
        expect(body.postings).toEqual([{ legId: null, counterpartyAccountId: SAVINGS, amount: -100 }]);
    });
});

// ADR-0049 D10 (amended 2026-11-17): every estimating series' occurrence opens on its
// estimate, the figure the subtitle prints and auto-post writes, in one currency too.
// Mocked at fetch, with every read answering at once.
describe('Post or skip… on an estimating series in one currency', () => {
    it('opens the Amount on the estimate and posts it', async () => {
        const one = stubFetch()
            .on('GET', /\/accounts$/, ACCOUNTS)
            .on('GET', /\/payees$/, [])
            .on('GET', /\/tags$/, [])
            .on('GET', /\/reminders\/r1$/, DETAIL)
            .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
            .on('POST', /\/fire\/bank$/, { headerId: 'h1', skippedEarlierCount: 0, skippedEarlierFrom: null });
        renderModal({
            ...OCC, amount: -950, sourceAccountId: CHECKING, sourceAccountName: 'Account A', currencyCode: 'EUR',
            estimate: { requestedSampleCount: 3, availableSampleCount: 3, amount: -950, unavailableReason: null },
        });

        // Anchor: the subtitle prints the estimate, and the editor is up.
        expect(subtitle()).toHaveTextContent(`≈${formatSignedAmount(-950, 'EUR')}`);
        expect(await screen.findByLabelText('Amount')).toHaveValue(-950);
        fireEvent.click(screen.getByRole('button', { name: 'Post' }));
        await waitFor(() => expect(one.callsTo('POST', /\/fire\/bank$/)).toHaveLength(1));
        const body = one.callsTo('POST', /\/fire\/bank$/)[0]!.body as FireBankReminderRequest;
        expect(body.postings).toEqual([{ legId: null, counterpartyAccountId: RENT, amount: -950 }]);
    });
});
