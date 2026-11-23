import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ReminderEditorDialog } from './ReminderEditorDialog';
import { problem, stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type {
    AccountSummary,
    CreateReminderRequest,
    EditInvestmentReminderRequest,
    EditReminderRequest,
    ReminderDetail,
} from '@/lib/types';

/**
 * The reminder editor's Tags field, wired end to end through the real bank
 * editor and the real API client, mocked at `fetch` because the server half
 * (template tags) is not built yet.
 *
 * Create sends the template's memo and tags as `notes`. Edit sends the
 * transaction's memo and tags only when the user changed them, so an untouched
 * save never clears what the template holds, names each stored line by its
 * source leg, and restates every line's memo, because the server rebuilds the
 * lines.
 */

const LEDGER = 'ledger-1';
const CHECKING = 'acct-bank';
const RENT = 'cat-rent';
const UTILITIES = 'cat-utilities';
const BROKERAGE = 'acct-brokerage';

const base = {
    ledgerId: LEDGER, parentId: null, categoryKind: null, currencyCode: 'USD',
    isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
    holdingsAccountId: null, isTradeCommission: false,
} satisfies Omit<AccountSummary, 'id' | 'name' | 'accountType'>;

const ACCOUNTS: AccountSummary[] = [
    { ...base, id: CHECKING, name: 'Checking', accountType: 'bank' },
    { ...base, id: RENT, name: 'Rent', accountType: 'category', categoryKind: 'expense' },
    { ...base, id: UTILITIES, name: 'Utilities', accountType: 'category', categoryKind: 'expense' },
    { ...base, id: BROKERAGE, name: 'Brokerage', accountType: 'investment' },
];

const leg = (
    accountId: string, accountName: string, postingIndex: number, amount: number, legMemo: string | null,
    legTags: string[] = [],
) => ({
    legId: `tleg-${postingIndex}-${accountName}`,
    accountId, accountName, postingIndex, amount, legMemo, legTags,
    securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null,
});

// A two-line split template: the transaction memo is on the template's
// header and each line's own memo on its Checking leg, where the write
// primitive places them.
const DETAIL: ReminderDetail = {
    id: 'r1', kind: 'bank', payee: 'Landlord', memo: 'Flat 2', tags: [], checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null,
    nextDueDate: '2026-11-01', autoCommitDaysBefore: null, isActive: true,
    isLoanReminder: false, origin: 'manual', sourceAccountId: CHECKING,
    legs: [
        leg(CHECKING, 'Checking', 0, -600, 'Base rent'),
        leg(RENT, 'Rent', 0, 600, null),
        leg(CHECKING, 'Checking', 1, -300, 'Water'),
        leg(UTILITIES, 'Utilities', 1, 300, null),
    ],
};

// Three lines, each with its own memo on its Checking leg.
const THREE_LINES: ReminderDetail = {
    ...DETAIL,
    id: 'r3', memo: null,
    legs: [
        leg(CHECKING, 'Checking', 0, -100, 'a'),
        leg(RENT, 'Rent', 0, 100, null),
        leg(CHECKING, 'Checking', 1, -200, 'b'),
        leg(UTILITIES, 'Utilities', 1, 200, null),
        leg(CHECKING, 'Checking', 2, -300, 'c'),
        leg(RENT, 'Rent', 2, 300, null),
    ],
};

// A one-posting event: the header is not in play, so the event's memo and tags
// are on its brokerage leg and the header's own are empty.
const INVESTMENT_DETAIL: ReminderDetail = {
    ...DETAIL,
    id: 'r2', kind: 'investment', payee: 'Contribution', memo: null, action: 'transfer',
    sourceAccountId: BROKERAGE,
    legs: [
        { ...leg(BROKERAGE, 'Brokerage', 0, 500, 'Monthly'), postingRole: 'transfer' },
        { ...leg(CHECKING, 'Checking', 0, -500, null), postingRole: 'transfer' },
    ],
};

// Templates holding tags: the transaction's set and each line's own, read from
// the source side. The category legs carry sets of their own, which this
// editor does not edit and must not show.
const TAGGED_SPLIT: ReminderDetail = {
    ...DETAIL,
    id: 'r4', tags: ['Housing'],
    legs: [
        leg(CHECKING, 'Checking', 0, -600, 'Base rent', ['Fixed']),
        leg(RENT, 'Rent', 0, 600, null, ['Elsewhere']),
        leg(CHECKING, 'Checking', 1, -300, 'Water', ['Shared']),
        leg(UTILITIES, 'Utilities', 1, 300, null),
    ],
};

// A one-line template has no header notes: its memo and tags are the Checking
// leg's own, and the header's own are empty.
const TAGGED_ONE_LINE: ReminderDetail = {
    ...DETAIL,
    id: 'r5', memo: null, tags: [],
    legs: [
        leg(CHECKING, 'Checking', 0, -900, 'Flat 2', ['Housing']),
        leg(RENT, 'Rent', 0, 900, null, ['Elsewhere']),
    ],
};

const TAGGED_INVESTMENT: ReminderDetail = {
    ...INVESTMENT_DETAIL,
    id: 'r6', tags: [],
    legs: [
        { ...leg(BROKERAGE, 'Brokerage', 0, 500, 'Monthly', ['Housing']), postingRole: 'transfer' },
        { ...leg(CHECKING, 'Checking', 0, -500, null, ['Elsewhere']), postingRole: 'transfer' },
    ],
};

function serve(stub: FetchStub) {
    stub
        .on('GET', /\/accounts$/, ACCOUNTS)
        .on('GET', /\/payees$/, [])
        .on('GET', /\/tags$/, [
            { id: 't1', name: 'Housing', color: null, usageCount: 1, transactionCount: 1 },
            { id: 't2', name: 'Fixed', color: null, usageCount: 1, transactionCount: 1 },
            { id: 't3', name: 'Shared', color: null, usageCount: 1, transactionCount: 1 },
            { id: 't4', name: 'Elsewhere', color: null, usageCount: 1, transactionCount: 1 },
        ])
        .on('GET', /\/reminders\/r4$/, TAGGED_SPLIT)
        .on('PATCH', /\/reminders\/r4$/, TAGGED_SPLIT)
        .on('GET', /\/reminders\/r5$/, TAGGED_ONE_LINE)
        .on('PATCH', /\/reminders\/r5$/, TAGGED_ONE_LINE)
        .on('GET', /\/reminders\/r6$/, TAGGED_INVESTMENT)
        .on('PATCH', /\/reminders\/r6\/investment$/, TAGGED_INVESTMENT)
        .on('GET', /\/reminders\/r1$/, DETAIL)
        .on('GET', /\/frequent-counterparties$/, { accounts: [], categories: [] })
        .on('GET', /\/similar-payees$/, [])
        .on('GET', /\/merge-candidates$/, [])
        .on('GET', /\/reminders\/r2$/, INVESTMENT_DETAIL)
        .on('GET', /\/reminders\/r3$/, THREE_LINES)
        .on('PATCH', /\/reminders\/r3$/, THREE_LINES)
        .on('GET', /\/securities$/, [])
        .on('GET', /\/holdings$/, {
            accountId: BROKERAGE, accountName: 'Brokerage', currencyCode: 'USD',
            summary: { portfolioValue: 0, costBasis: 0, unrealizedGain: 0, percentChange: 0, cashBalance: 0, total: 0 },
            positions: [],
        })
        .on('POST', /\/reminders$/, DETAIL)
        .on('PATCH', /\/reminders\/r1$/, DETAIL)
        .on('PATCH', /\/reminders\/r2\/investment$/, INVESTMENT_DETAIL);
}

function renderDialog(props: Partial<Parameters<typeof ReminderEditorDialog>[0]> = {}) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={qc}>
            <ReminderEditorDialog ledgerId={LEDGER} onClose={() => {}} onSaved={() => {}} {...props} />
        </QueryClientProvider>,
    );
}

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    serve(stub);
});
afterEach(() => {
    vi.unstubAllGlobals();
});

describe('creating a reminder', () => {
    it('sends the template\'s memo and tags as notes', async () => {
        renderDialog({
            fromTransaction: {
                sourceAccountId: CHECKING,
                prefill: {
                    payee: 'Landlord',
                    memo: 'Flat 2',
                    tags: ['Housing'],
                    postings: [{ counterpartyAccountId: RENT, amount: -900 }],
                },
            },
        });

        await userEvent.click(await screen.findByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('POST', /\/reminders$/)).toHaveLength(1));
        const body = stub.callsTo('POST', /\/reminders$/)[0]!.body as CreateReminderRequest;
        expect(body.notes).toEqual({ side: CHECKING, memo: 'Flat 2', tags: ['Housing'] });
        expect(body.postings).toEqual([{ legId: null, counterpartyAccountId: RENT, amount: -900 }]);
        expect('memo' in body).toBe(false);
    });

    it('carries a tag picked in the embedded editor', async () => {
        renderDialog({
            fromTransaction: {
                sourceAccountId: CHECKING,
                prefill: { payee: 'Landlord', postings: [{ counterpartyAccountId: RENT, amount: -900 }] },
            },
        });

        await userEvent.click(await screen.findByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Fixed' }));
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('POST', /\/reminders$/)).toHaveLength(1));
        expect((stub.callsTo('POST', /\/reminders$/)[0]!.body as CreateReminderRequest).notes)
            .toEqual({ side: CHECKING, tags: ['Fixed'] });
    });
});

describe('editing a reminder', () => {
    // The server rebuilds a template's lines on every edit, so each edit
    // carries each line's memo and tags as well as naming the stored lines by
    // leg id.
    const BOTH_LINES = [{ index: 0, memo: 'Base rent', tags: [] }, { index: 1, memo: 'Water', tags: [] }];

    async function patchedNotes() {
        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r1$/)).toHaveLength(1));
        return (stub.callsTo('PATCH', /\/reminders\/r1$/)[0]!.body as EditReminderRequest).notes;
    }

    it('an untouched save leaves the template\'s memo and tags alone, and restates each line\'s memo', async () => {
        renderDialog({ reminderId: 'r1' });

        expect(await screen.findByLabelText('Memo (whole transaction)')).toHaveValue('Flat 2');
        expect(screen.getByLabelText('Posting 2 memo')).toHaveValue('Water');
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        expect(await patchedNotes()).toEqual({ side: CHECKING, lines: BOTH_LINES });
    });

    it('clearing the memo sends null, which clears it', async () => {
        renderDialog({ reminderId: 'r1' });

        fireEvent.change(await screen.findByLabelText('Memo (whole transaction)'), { target: { value: '' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        expect(await patchedNotes()).toEqual({ side: CHECKING, memo: null, lines: BOTH_LINES });
    });

    it('clearing a line\'s memo sends that line\'s clear, next to the line left alone', async () => {
        renderDialog({ reminderId: 'r1' });

        fireEvent.change(await screen.findByLabelText('Posting 2 memo'), { target: { value: '' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        expect(await patchedNotes())
            .toEqual({ side: CHECKING, lines: [{ index: 0, memo: 'Base rent', tags: [] }, { index: 1, memo: null, tags: [] }] });
    });

    it('a line added after one is removed sends itself blank, so it cannot take a removed line\'s values', async () => {
        // Removing the middle line and adding one puts the new line at the
        // last stored line's position. Left out, it could only be matched to
        // a stored line by that position, and would take its memo and tags.
        renderDialog({ reminderId: 'r3' });

        expect(await screen.findByLabelText('Posting 3 memo')).toHaveValue('c');
        await userEvent.click(screen.getByLabelText('Actions for posting 2'));
        await userEvent.click(await screen.findByRole('menuitem', { name: /remove split/i }));
        await waitFor(() => expect(screen.queryByLabelText('Posting 3 memo')).not.toBeInTheDocument());

        await userEvent.click(screen.getByLabelText('Add another posting'));
        await userEvent.click(await screen.findByLabelText('Posting 3 category'));
        await userEvent.click(await screen.findByRole('option', { name: /Rent/ }));
        fireEvent.change(screen.getByLabelText('Posting 3 amount'), { target: { value: '-50' } });
        expect(screen.getByLabelText('Posting 3 memo')).toHaveValue('');
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r3$/)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/reminders\/r3$/)[0]!.body as EditReminderRequest;
        expect(body.notes).toEqual({
            side: CHECKING,
            lines: [
                { index: 0, memo: 'a', tags: [] },
                { index: 1, memo: 'c', tags: [] },
                { index: 2, memo: null, tags: [] },
            ],
        });
    });

    it('names each stored line by its source leg, so its values follow it and a line just added takes none', async () => {
        // The template's lines are rebuilt on every edit. What ties a rebuilt
        // line to a stored one is the stored line's leg on the source; without
        // it, the line moved into a removed line's position would take that
        // line's memo and tags on both sides, including the ones this editor
        // cannot see.
        renderDialog({ reminderId: 'r3' });

        expect(await screen.findByLabelText('Posting 3 memo')).toHaveValue('c');
        await userEvent.click(screen.getByLabelText('Actions for posting 2'));
        await userEvent.click(await screen.findByRole('menuitem', { name: /remove split/i }));
        await waitFor(() => expect(screen.queryByLabelText('Posting 3 memo')).not.toBeInTheDocument());

        await userEvent.click(screen.getByLabelText('Add another posting'));
        await userEvent.click(await screen.findByLabelText('Posting 3 category'));
        await userEvent.click(await screen.findByRole('option', { name: /Rent/ }));
        fireEvent.change(screen.getByLabelText('Posting 3 amount'), { target: { value: '-50' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r3$/)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/reminders\/r3$/)[0]!.body as EditReminderRequest;
        expect(body.postings?.items.map((i) => i.legId)).toEqual([
            'tleg-0-Checking',
            'tleg-2-Checking',
            null,
        ]);
    });
});

describe('a refused edit', () => {
    // A save the server refuses (here a line's account deactivated since the
    // editor opened) is shown in the server's words, and the dialog stays open
    // with what the user typed, so the user can correct it and save again.
    const REFUSAL = 'A posting\'s counterparty account is inactive; reactivate it before posting to it.';

    // The message is shown once, in the dialog's own alert above the Schedule
    // section, as the investment editor shows it; the embedded bank editor
    // does not repeat it.
    async function expectShownOnce(message: string) {
        expect(await screen.findAllByText(message)).toHaveLength(1);
        const holding = screen.getAllByRole('alert').filter((a) => a.textContent?.includes(message));
        expect(holding).toHaveLength(1);
        expect(holding[0]!.compareDocumentPosition(screen.getByText('Schedule')) & Node.DOCUMENT_POSITION_FOLLOWING)
            .toBeTruthy();
    }

    it('shows the server\'s message, keeps the form as the user left it, and stays open', async () => {
        stub.on('PATCH', /\/reminders\/r1$/, problem(422, REFUSAL, 'account-inactive'));
        const onClose = vi.fn();
        const onSaved = vi.fn();
        renderDialog({ reminderId: 'r1', onClose, onSaved });

        fireEvent.change(await screen.findByLabelText('Posting 2 memo'), { target: { value: 'Water and sewer' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await expectShownOnce(REFUSAL);
        expect(stub.callsTo('PATCH', /\/reminders\/r1$/)).toHaveLength(1);
        expect(onClose).not.toHaveBeenCalled();
        expect(onSaved).not.toHaveBeenCalled();
        expect(screen.getByLabelText('Memo (whole transaction)')).toHaveValue('Flat 2');
        expect(screen.getByLabelText('Posting 1 memo')).toHaveValue('Base rent');
        expect(screen.getByLabelText('Posting 2 memo')).toHaveValue('Water and sewer');
        expect(screen.getByRole('button', { name: /^save$/i })).toBeEnabled();
    });

    it('a refused create shows the server\'s message once, too', async () => {
        stub.on('POST', /\/reminders$/, problem(422, REFUSAL, 'account-inactive'));
        renderDialog({
            fromTransaction: {
                sourceAccountId: CHECKING,
                prefill: { payee: 'Landlord', postings: [{ counterpartyAccountId: RENT, amount: -900 }] },
            },
        });

        await userEvent.click(await screen.findByRole('button', { name: /^save$/i }));

        await expectShownOnce(REFUSAL);
        expect(stub.callsTo('POST', /\/reminders$/)).toHaveLength(1);
    });
});

describe('editing a reminder whose template holds tags', () => {
    // The editor opens with the stored sets, so a picked tag adds to what is
    // shown, and a line restating its tags restates what it holds.

    it('shows the transaction\'s tags and each line\'s own, from the source side', async () => {
        renderDialog({ reminderId: 'r4' });

        expect(await screen.findByLabelText('Memo (whole transaction)')).toHaveValue('Flat 2');
        expect(screen.getByTitle('Housing')).toBeInTheDocument();
        expect(within(screen.getByLabelText('Posting 1 tags')).getByTitle('Fixed')).toBeInTheDocument();
        expect(within(screen.getByLabelText('Posting 2 tags')).getByTitle('Shared')).toBeInTheDocument();
        expect(screen.queryByTitle('Elsewhere')).not.toBeInTheDocument();
    });

    it('a tag picked for the transaction is sent with the stored ones, and every line sends its own', async () => {
        renderDialog({ reminderId: 'r4' });

        await userEvent.click(await screen.findByRole('button', { name: 'Edit Tags (whole transaction)' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Fixed' }));
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r4$/)).toHaveLength(1));
        expect((stub.callsTo('PATCH', /\/reminders\/r4$/)[0]!.body as EditReminderRequest).notes).toEqual({
            side: CHECKING,
            tags: ['Housing', 'Fixed'],
            lines: [
                { index: 0, memo: 'Base rent', tags: ['Fixed'] },
                { index: 1, memo: 'Water', tags: ['Shared'] },
            ],
        });
    });

    it('a tag picked for a line is sent with that line\'s stored ones', async () => {
        renderDialog({ reminderId: 'r4' });

        await userEvent.click(await screen.findByRole('button', { name: 'Edit Posting 2 tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Housing' }));
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r4$/)).toHaveLength(1));
        expect((stub.callsTo('PATCH', /\/reminders\/r4$/)[0]!.body as EditReminderRequest).notes).toEqual({
            side: CHECKING,
            lines: [
                { index: 0, memo: 'Base rent', tags: ['Fixed'] },
                { index: 1, memo: 'Water', tags: ['Shared', 'Housing'] },
            ],
        });
    });

    async function patchedOneLine() {
        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r5$/)).toHaveLength(1));
        return stub.callsTo('PATCH', /\/reminders\/r5$/)[0]!.body as EditReminderRequest;
    }

    it('on a one-line template, the Memo and Tags fields show the source line\'s own', async () => {
        renderDialog({ reminderId: 'r5' });

        expect(await screen.findByLabelText('Memo')).toHaveValue('Flat 2');
        expect(screen.getByTitle('Housing')).toBeInTheDocument();
        expect(screen.queryByTitle('Elsewhere')).not.toBeInTheDocument();
    });

    it('on a one-line template, an untouched save sends no memo or tags', async () => {
        renderDialog({ reminderId: 'r5' });

        expect(await screen.findByLabelText('Memo')).toHaveValue('Flat 2');
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        expect((await patchedOneLine()).notes).toEqual({ side: CHECKING });
    });

    it('on a one-line template, a picked tag is sent with the stored ones', async () => {
        renderDialog({ reminderId: 'r5' });

        expect(await screen.findByTitle('Housing')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Fixed' }));
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        expect((await patchedOneLine()).notes).toEqual({ side: CHECKING, tags: ['Housing', 'Fixed'] });
    });

    it('splitting a one-line template keeps its notes as the transaction\'s and copies them onto line 1', async () => {
        // A split copies the side's memo and tags to the transaction and to the
        // original line, and a new line starts blank. The editor shows that
        // before Save, and the rebuilt lines store what it shows.
        renderDialog({ reminderId: 'r5' });

        expect(await screen.findByLabelText('Memo')).toHaveValue('Flat 2');
        await userEvent.click(screen.getByRole('button', { name: /split this transaction/i }));

        expect(await screen.findByLabelText('Memo (whole transaction)')).toHaveValue('Flat 2');
        expect(screen.getByLabelText('Posting 1 memo')).toHaveValue('Flat 2');
        expect(within(screen.getByLabelText('Posting 1 tags')).getByTitle('Housing')).toBeInTheDocument();
        // Once for the transaction, once for line 1.
        expect(screen.getAllByTitle('Housing')).toHaveLength(2);
        expect(screen.getByLabelText('Posting 2 memo')).toHaveValue('');
        expect(within(screen.getByLabelText('Posting 2 tags')).queryByTitle('Housing')).not.toBeInTheDocument();

        await userEvent.click(await screen.findByLabelText('Posting 2 category'));
        await userEvent.click(await screen.findByRole('option', { name: /Utilities/ }));
        fireEvent.change(screen.getByLabelText('Posting 2 amount'), { target: { value: '-50' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        const body = await patchedOneLine();
        expect(body.notes).toEqual({
            side: CHECKING,
            lines: [
                { index: 0, memo: 'Flat 2', tags: ['Housing'] },
                { index: 1, memo: null, tags: [] },
            ],
        });
        expect(body.postings?.items.map((i) => i.legId)).toEqual(['tleg-0-Checking', null]);
    });

    it('on an investment template, the event\'s memo and tags come from the brokerage leg', async () => {
        renderDialog({ reminderId: 'r6' });

        expect(await screen.findByLabelText('Memo')).toHaveValue('Monthly');
        expect(screen.getByTitle('Housing')).toBeInTheDocument();
        expect(screen.queryByTitle('Elsewhere')).not.toBeInTheDocument();
    });

    it('on an investment template, a picked tag is sent with the stored ones', async () => {
        renderDialog({ reminderId: 'r6' });

        expect(await screen.findByTitle('Housing')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: 'Edit Tags' }));
        await userEvent.click(await screen.findByRole('checkbox', { name: 'Fixed' }));
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r6\/investment$/)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/reminders\/r6\/investment$/)[0]!.body as EditInvestmentReminderRequest;
        expect(body.transaction!.tags).toEqual(['Housing', 'Fixed']);
    });

    it('an untouched investment save leaves the stored tags alone', async () => {
        renderDialog({ reminderId: 'r6' });

        expect(await screen.findByTitle('Housing')).toBeInTheDocument();
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r6\/investment$/)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/reminders\/r6\/investment$/)[0]!.body as EditInvestmentReminderRequest;
        expect('tags' in body.transaction!).toBe(false);
    });
});

describe('editing an investment reminder', () => {
    it('an untouched save sends neither memo nor tags, so the template keeps both', async () => {
        renderDialog({ reminderId: 'r2' });

        expect(await screen.findByLabelText('Memo')).toHaveValue('Monthly');
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r2\/investment$/)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/reminders\/r2\/investment$/)[0]!.body as EditInvestmentReminderRequest;
        expect(body.transaction).toBeDefined();
        expect('memo' in body.transaction!).toBe(false);
        expect('tags' in body.transaction!).toBe(false);
    });

    it('sends a changed memo', async () => {
        renderDialog({ reminderId: 'r2' });

        fireEvent.change(await screen.findByLabelText('Memo'), { target: { value: 'Quarterly' } });
        await userEvent.click(screen.getByRole('button', { name: /^save$/i }));

        await waitFor(() => expect(stub.callsTo('PATCH', /\/reminders\/r2\/investment$/)).toHaveLength(1));
        const body = stub.callsTo('PATCH', /\/reminders\/r2\/investment$/)[0]!.body as EditInvestmentReminderRequest;
        expect(body.transaction!.memo).toBe('Quarterly');
        expect('tags' in body.transaction!).toBe(false);
    });
});
