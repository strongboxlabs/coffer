import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type FetchStub, type StubbedCall } from '@/lib/annotations/fetchStub';
import { formatCurrency } from '@/lib/money';
import type { AccountSummary, EditReminderRequest, ReminderDetail } from '@/lib/types';

import { ReminderEditorDialog } from './ReminderEditorDialog';

/**
 * The series editor prints its figures in the source account's currency. A
 * deactivated source is not in the account list the editor picks from, so its
 * currency is read from the list that keeps inactive accounts, and the editor
 * prints no figure until that list answers. A source no list holds is a failed
 * load. Mocked at `fetch`, through the real bank editor.
 */

const LEDGER = 'ledger-1';
const SOURCE = 'acct-source';
const CATEGORY_A = 'cat-a';
const CATEGORY_B = 'cat-b';

const base = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'EUR',
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
    holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

/** The series' source, deactivated: only the inactive-inclusive list holds it. */
const SOURCE_ACCOUNT: AccountSummary = { ...base, id: SOURCE, name: 'Account A', accountType: 'bank', isActive: false };
const CATEGORIES: AccountSummary[] = [
    { ...base, id: CATEGORY_A, name: 'Category A', accountType: 'category', categoryKind: 'expense' },
    { ...base, id: CATEGORY_B, name: 'Category B', accountType: 'category', categoryKind: 'expense' },
];

const leg = (accountId: string, accountName: string, postingIndex: number, amount: number) => ({
    legId: `tleg-${postingIndex}-${accountName}`, accountId, accountName, postingIndex, amount, legMemo: null,
    legTags: [], securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null,
});

/** A two-line template: the editor shows it as a split, whose sum it prints. */
const DETAIL: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Payee A', memo: null, tags: [], checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-11-01', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: SOURCE,
    legs: [
        leg(SOURCE, 'Account A', 0, -600), leg(CATEGORY_A, 'Category A', 0, 600),
        leg(SOURCE, 'Account A', 1, -300), leg(CATEGORY_B, 'Category B', 1, 300),
    ],
};

let stub: FetchStub;

/** The active list never holds the source; the inactive-inclusive one holds `every`. */
function serve(every: AccountSummary[]) {
    stub
        .on('GET', /\/accounts$/, (call: StubbedCall) =>
            (call.search.get('includeInactive') === 'true' ? every : CATEGORIES))
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [])
        .on('GET', /\/reminders\/r1$/, DETAIL)
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, []);
}

/** Holds the inactive-inclusive account read until `open` lets it answer; `asked`
 *  says whether it has been sent. */
function holdEveryAccount(): { open: () => void; asked: () => boolean } {
    let open!: () => void;
    let asked = false;
    const gate = new Promise<void>((resolve) => { open = resolve; });
    const answer = globalThis.fetch;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        const url = new URL(raw, 'http://localhost');
        if (url.pathname.endsWith('/accounts') && url.searchParams.get('includeInactive') === 'true') {
            asked = true;
            await gate;
        }
        return answer(input, init);
    });
    return { open, asked: () => asked };
}

function renderDialog() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={qc}>
            <ReminderEditorDialog ledgerId={LEDGER} reminderId="r1" onClose={() => {}} onSaved={() => {}} />
        </QueryClientProvider>,
    );
}

/** The split's sum, printed in the source's currency beside the payee and under the
 *  lines: its text, once per place. */
const totals = async () => (await screen.findAllByTitle('Sum of postings (saved as the transaction total)'))
    .map((e) => e.textContent);
const accountReads = () => stub.callsTo('GET', /\/accounts$/).map((c) => c.search.get('includeInactive'));

beforeEach(() => {
    stub = stubFetch();
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('editing a series whose source is deactivated', () => {
    it('prints its figures in the source\'s currency, read from the list that keeps inactive accounts', async () => {
        serve([SOURCE_ACCOUNT, ...CATEGORIES]);
        renderDialog();

        expect(await totals()).toEqual([formatCurrency(-900, 'EUR'), formatCurrency(-900, 'EUR')]);
        expect(screen.queryByText(/\$/)).toBeNull();
        expect(accountReads()).toEqual([null, 'true']);
        expect(stub.unrouted).toEqual([]);
    });

    it('prints no figure while that list is still answering', async () => {
        serve([SOURCE_ACCOUNT, ...CATEGORIES]);
        const everyAccount = holdEveryAccount();
        renderDialog();

        // Asked only once the series and the active list have answered; the page
        // then settles on every read but that one.
        await waitFor(() => expect(everyAccount.asked()).toBe(true));
        await new Promise((resolve) => setTimeout(resolve, 50));
        expect(stub.callsTo('GET', /\/payees$/)).toHaveLength(1);
        expect(screen.getByText('Loading…')).toBeInTheDocument();
        expect(screen.queryByRole('alert')).toBeNull();
        expect(screen.queryByText(/\$/)).toBeNull();

        everyAccount.open();

        expect(await totals()).toEqual([formatCurrency(-900, 'EUR'), formatCurrency(-900, 'EUR')]);
        expect(screen.queryByText('Loading…')).toBeNull();
        expect(screen.queryByText(/\$/)).toBeNull();
    });

    it('is a failed load when no list holds the source', async () => {
        serve(CATEGORIES);
        renderDialog();

        expect(await screen.findByRole('alert')).toHaveTextContent('Could not load accounts or the reminder.');
        expect(screen.queryByText('Loading…')).toBeNull();
        expect(screen.queryByText(/\$/)).toBeNull();
    });
});

// ADR-0114 D14: the series editor seeds both amounts of a transfer between currencies from
// its template, through the same prefill as Post or skip…, and never refills them. ADR-0114
// D13 and board OT-4: with Estimate from history chosen, the Schedule says where the other
// side's figure comes from.
describe('editing a series that transfers between currencies', () => {
    const USD_BANK = 'acct-usd';
    const EUR_BANK = 'acct-eur';
    const CATEGORY = 'cat-a';
    const ACCOUNTS: AccountSummary[] = [
        { ...base, id: USD_BANK, name: 'Test USD Bank', accountType: 'bank', currencyCode: 'USD' },
        { ...base, id: EUR_BANK, name: 'Test EUR Bank', accountType: 'bank', currencyCode: 'EUR' },
        { ...base, id: CATEGORY, name: 'Category A', accountType: 'category', categoryKind: 'expense', currencyCode: 'USD' },
    ];
    const detail = (counterpartId: string, name: string, amount: number, estimating = false): ReminderDetail => ({
        ...DETAIL, sourceAccountId: USD_BANK,
        legs: [
            { ...leg(USD_BANK, 'Test USD Bank', 0, -100), legId: 'tleg-source' },
            { ...leg(counterpartId, name, 0, amount), legId: 'tleg-other' },
        ],
        ...(estimating
            ? { estimate: { requestedSampleCount: 3, availableSampleCount: 3, amount: -100, unavailableReason: null } }
            : {}),
    });

    function serveSeries(d: ReminderDetail) {
        stub
            .on('GET', /\/accounts$/, ACCOUNTS)
            .on('GET', /\/payees$/, [])
            .on('GET', /\/tags$/, [])
            .on('GET', /\/reminders\/r1$/, d)
            .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
            .on('PATCH', /\/reminders\/r1$/, d);
    }

    async function saved(): Promise<EditReminderRequest> {
        fireEvent.click(await screen.findByRole('button', { name: 'Save' }));
        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r1$/)).toHaveLength(1));
        return stub.callsTo('PATCH', /\/reminders\/r1$/)[0]!.body as EditReminderRequest;
    }

    it('seeds Received from the template, asks for no rate, and an untouched save resends the stored pair', async () => {
        serveSeries(detail(EUR_BANK, 'Test EUR Bank', 90));
        renderDialog();

        const field = await screen.findByLabelText('Amount received in Test EUR Bank, in EUR');
        expect((field as HTMLInputElement).value).toBe('90.00');
        const body = await saved();
        expect(body.postings?.items).toEqual([{
            legId: 'tleg-source', counterpartyAccountId: EUR_BANK, amount: -100, counterpartyAmount: 90,
        }]);
        expect(stub.callsTo('GET', /\/derived$/)).toHaveLength(0);
    });

    it('saves a same-currency template as before, with no second amount', async () => {
        serveSeries(detail(CATEGORY, 'Category A', 100));
        renderDialog();

        // Anchor: the editor is up, with the template's Amount.
        expect(await screen.findByLabelText('Amount')).toHaveValue(-100);
        const body = await saved();
        expect(body.postings?.items).toEqual([{ legId: 'tleg-source', counterpartyAccountId: CATEGORY, amount: -100 }]);
    });

    it('says under Estimate from history where the other side\'s figure comes from', async () => {
        serveSeries(detail(EUR_BANK, 'Test EUR Bank', 90, true));
        renderDialog();

        await screen.findByLabelText('Amount received in Test EUR Bank, in EUR');
        const line = screen.getByText(
            'The amount received in Test EUR Bank is set from the stored USD to EUR rate on each due date. '
            + 'With no stored rate for that date, the occurrence is not posted until you enter that amount.');
        expect(screen.getByRole('radio', { name: 'Estimate from history' })).toHaveAttribute('aria-describedby', line.id);
    });

    it('says the same of the money sent from the other account when the transfer comes in from it', async () => {
        const d = detail(EUR_BANK, 'Test EUR Bank', -90, true);
        serveSeries({ ...d, legs: [{ ...d.legs[0]!, amount: 100 }, d.legs[1]!] });
        renderDialog();

        await screen.findByLabelText('Amount sent from Test EUR Bank, in EUR');
        const line = screen.getByText(
            'The amount sent from Test EUR Bank is set from the stored USD to EUR rate on each due date. '
            + 'With no stored rate for that date, the occurrence is not posted until you enter that amount.');
        expect(screen.getByRole('radio', { name: 'Estimate from history' })).toHaveAttribute('aria-describedby', line.id);
    });
});
