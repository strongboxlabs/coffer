import { describe, it, expect, beforeEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { BankRegisterPage } from './BankRegisterPage';
import * as apiModule from '@/lib/api';
import type {
    AccountSummary,
    BankRow,
    LedgerSummary,
    RegisterEntry,
} from '@/lib/types';

/**
 * Page-level coverage for SPLIT rows.
 *
 * `BankRegisterPage.test.tsx` next door contained zero occurrences of the word
 * "split" — the whole feature was covered by one pure assertion in
 * `lib/splitCollapse.test.ts` and nothing else. So the collapsed row's
 * contents, the expand toggle, the leg rows it inserts and the keyboard route
 * into the editor were all unasserted, which is how the expand button came to
 * sit on top of the category cell and the toggle's `aria-controls` came to
 * point at an element that has never existed.
 *
 * Kept in its own file rather than appended to the sibling: that file opens
 * with a deliberate statement of what it does and does not cover ("we don't
 * try to test the list virtualization directly"), and splits are a different
 * subject with a different fixture.
 */

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const ACCOUNT_ID = '00000000-0000-0000-0000-000000000100';
const HEADER_ID = '00000000-0000-0000-0000-0000000000aa';
const GROUP_ID = '00000000-0000-0000-0000-0000000000bb';

const SALARY_ID = '00000000-0000-0000-0000-000000000301';
const FED_TAX_ID = '00000000-0000-0000-0000-000000000302';
const HEALTH_ID = '00000000-0000-0000-0000-000000000303';

const TEST_LEDGER: LedgerSummary = { id: LEDGER_ID, name: 'Personal', role: 'owner' };

function account(over: Partial<AccountSummary> & { id: string; name: string }): AccountSummary {
    return {
        ledgerId: LEDGER_ID,
        parentId: null,
        accountType: 'category',
        categoryKind: 'expense',
        currencyCode: 'USD',
        isActive: true,
        isSystem: false,
        feedConnectionId: null,
        needsReviewCount: 0,
        holdingsAccountId: null,
        isTradeCommission: false,
        ...over,
    } as AccountSummary;
}

const CHECKING = account({
    id: ACCOUNT_ID, name: 'Checking', accountType: 'bank', categoryKind: null,
});
const ACCOUNTS: AccountSummary[] = [
    CHECKING,
    account({ id: SALARY_ID, name: 'Salary', categoryKind: 'income' }),
    account({ id: FED_TAX_ID, name: 'Federal Income Tax' }),
    account({ id: HEALTH_ID, name: 'Health Insurance' }),
];

function leg(over: Partial<BankRow> & { id: string }): BankRow {
    const defaults: BankRow = {
        kind: 'bank',
        id: '',
        accountId: ACCOUNT_ID,
        payee: 'Northwind Trading',
        memo: null,
        amount: 0,
        postedAt: '2026-05-01T12:00:00Z',
        transactedAt: null,
        status: 'uncleared',
        isHidden: false,
        hasOverrides: false,
        balanceAfter: 100,
        origin: 'manual',
        isPending: false,
        externalId: null,
        checkNumber: null,
        counterpartyId: '00000000-0000-0000-0000-000000000999',
        txnGroupId: GROUP_ID,
        legIndex: 0,
        counterpartyAccountId: null,
        counterpartyAccountName: null,
        counterpartyAccountType: 'category',
        tags: [],
        headerId: HEADER_ID,
        clearedAt: null,
        clearedByUserId: null,
        createdAt: '2026-05-01T12:00:00Z',
        legMemo: null,
        headerMemo: null,
        onlineMatchFitid: null,
        onlineMatchFiId: null,
        needsReview: false,
        providerRawPayload: null,
        headerAccountNetAmount: null,
        providerKey: null,
        isMergeWinner: false,
        importSource: null,
        derivedAction: null,
        accountPostingsOnHeader: 3,
        headerTotalPostings: 3,
    };
    return { ...defaults, ...over };
}

/**
 * A three-leg paycheck. Gross pay dominates by absolute amount, exactly the
 * shape the collapsed row's summary is meant to characterise.
 */
const PAYCHECK_LEGS: BankRow[] = [
    leg({
        id: 'leg-1', legIndex: 0, amount: 2000,
        counterpartyAccountId: SALARY_ID, counterpartyAccountName: 'Salary',
        legMemo: 'Gross pay',
    }),
    leg({
        id: 'leg-2', legIndex: 1, amount: -150,
        counterpartyAccountId: FED_TAX_ID, counterpartyAccountName: 'Federal Income Tax',
        legMemo: 'Federal Tax',
    }),
    leg({
        id: 'leg-3', legIndex: 2, amount: -50,
        counterpartyAccountId: HEALTH_ID, counterpartyAccountName: 'Health Insurance',
        legMemo: 'Medical Insurance',
    }),
];

const SPLIT_ENTRY: RegisterEntry = {
    kind: 'group', txn: null, groupId: GROUP_ID, legs: PAYCHECK_LEGS,
};

function renderRegister() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(['ledgers'], [TEST_LEDGER]);
    queryClient.setQueryData(['accounts', LEDGER_ID], ACCOUNTS);

    const root = createRootRoute();
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: BankRegisterPage,
    });
    const router = createRouter({
        routeTree: root.addChildren([registerRoute]),
        history: createMemoryHistory({
            initialEntries: [`/ledgers/${LEDGER_ID}/accounts/${ACCOUNT_ID}`],
        }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

/** Render, then hand back the collapsed row's disclosure control. */
async function openRegister(entries: RegisterEntry[] = [SPLIT_ENTRY]) {
    vi.spyOn(apiModule, 'fetchRegister').mockResolvedValue({
        entries,
        cursorForOlder: null,
        cursorForNewer: null,
    });
    renderRegister();
    // Named by the toggle's own text, which is exactly the contract the
    // investment page's tests already rely on.
    return screen.findByRole('button', { name: /3 splits/i });
}

beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([TEST_LEDGER]);
    vi.spyOn(apiModule, 'fetchAccounts').mockResolvedValue(ACCOUNTS);
});

describe('a collapsed split row answers the selection modifier', () => {
    it('toggles selection on Cmd-click, as a plain row and both investment rows do', async () => {
        // The modifier toggled selection on a plain bank row and on BOTH
        // investment row variants, but fell through to a plain focus here — so
        // the same gesture on two rows that look alike did two different
        // things, and the one it did nothing useful on was the row that stands
        // for a whole multi-leg header.
        const toggle = await openRegister();
        const parent = toggle.closest('[role="row"]') as HTMLElement;
        const box = within(parent).getByRole('checkbox');
        expect(box).not.toBeChecked();

        fireEvent.click(parent, { metaKey: true });

        await waitFor(() => expect(box).toBeChecked());
    });
});

describe('a collapsed split row says what it is', () => {
    it('names NO single category, because a split does not have one', async () => {
        // #547 led this row with "the category that stands for the group" —
        // the filter-matched leg, else the largest by absolute summed amount.
        // Removed 2026-09-25: it was never requested, and it read as a claim
        // rather than a summary. The chip sat at the same left edge as a flat
        // row's category chip, which is what made the column read straight
        // down — and what made a heuristic pick look like the definite answer
        // a flat row gives. This paycheck has three categories; naming one of
        // them there says something untrue.
        //
        // Asserting the ABSENCE of all three, not just one, so a partial
        // reinstatement fails too.
        const btn = await openRegister();
        const parentRow = btn.closest('[role="row"]') as HTMLElement;

        expect(parentRow).not.toHaveTextContent('Salary');
        expect(parentRow).not.toHaveTextContent('Federal Income Tax');
        expect(parentRow).not.toHaveTextContent('Medical Insurance');
    });

    it('keeps the leg count in the toggle, where a screen reader still reads it', async () => {
        // The count must stay in TEXT, not an aria-label: it is the only
        // thing the collapsed row says about its contents, so a screen
        // reader has to reach it as content rather than as a button name.
        expect(await openRegister()).toHaveTextContent('3 splits');
    });

    it('claims no aria-controls, because there is no element to control', async () => {
        // The expanded content is N sibling rows inside a virtualised list.
        // A previous version pointed at `split-group-<id>`, which nothing has
        // ever rendered; a dangling IDREF is worse than an absent attribute.
        const btn = await openRegister();
        expect(btn).not.toHaveAttribute('aria-controls');
        expect(btn).toHaveAttribute('aria-expanded', 'false');
    });
});

describe('expanding a split', () => {
    it('inserts the leg rows and takes them away again', async () => {
        const user = userEvent.setup();
        const btn = await openRegister();

        // Anchored on a leg's own memo — data that exists ONLY on a leg row,
        // so its absence beforehand is meaningful rather than a pending frame.
        expect(screen.queryByText('Federal Tax')).toBeNull();

        await user.click(btn);
        await waitFor(() => expect(btn).toHaveAttribute('aria-expanded', 'true'));
        expect(screen.getByText('Federal Tax')).toBeInTheDocument();
        expect(screen.getByText('Medical Insurance')).toBeInTheDocument();

        await user.click(btn);
        await waitFor(() => expect(btn).toHaveAttribute('aria-expanded', 'false'));
        expect(screen.queryByText('Federal Tax')).toBeNull();
    });
});

describe('the keyboard route into a split', () => {
    it('opens the editor on Enter, which used to be a dead key', async () => {
        // Enter on a split parent was a deliberate no-op whose comment said it
        // stood "until the split-edit slice lands". That slice landed; the
        // no-op did not. With the context menu also carrying no Edit item, a
        // keyboard user had NO route to a split's legs at all.
        const user = userEvent.setup();
        const btn = await openRegister();

        // Focus the row the way a pointer user would, then use the keyboard.
        await user.click(btn.closest('[role="row"]')!);
        await user.keyboard('{Enter}');

        // The editor replaces the group's footprint — its leg grid is the
        // thing to assert on, not the register row.
        expect(await screen.findByLabelText('Posting 1 amount')).toBeInTheDocument();
        expect(screen.getByLabelText('Posting 3 amount')).toBeInTheDocument();
    });

    it('does not ALSO open the editor when Enter activates the toggle itself', async () => {
        // The guard that makes the above safe. Enter is the expand button's
        // native activation key, so without a BUTTON check one keypress would
        // both toggle the split and open the editor.
        const user = userEvent.setup();
        const btn = await openRegister();
        btn.focus();
        await user.keyboard('{Enter}');

        await waitFor(() => expect(btn).toHaveAttribute('aria-expanded', 'true'));
        expect(screen.queryByLabelText('Posting 1 amount')).toBeNull();
    });
});

describe('ArrowRight / ArrowLeft on a split parent', () => {
    it('expands on ArrowRight and collapses on ArrowLeft', async () => {
        // These are the conventional treegrid disclosure keys and were unbound
        // everywhere in the SPA. #547 gave the split parent Enter-to-edit but
        // left expansion reachable from the keyboard only by Tabbing to the
        // toggle — which works, and is not what a keyboard user reaches for.
        const user = userEvent.setup();
        const btn = await openRegister();

        // Focus the ROW, not the toggle — the toggle is deliberately excluded
        // (see the BUTTON test below).
        await user.click(btn.closest('[role="row"]')!);

        await user.keyboard('{ArrowRight}');
        await waitFor(() => expect(btn).toHaveAttribute('aria-expanded', 'true'));
        // Anchored on leg-only data, so its presence is meaningful rather than
        // a pending frame that happens to be empty.
        expect(screen.getByText('Federal Tax')).toBeInTheDocument();

        await user.keyboard('{ArrowLeft}');
        await waitFor(() => expect(btn).toHaveAttribute('aria-expanded', 'false'));
        expect(screen.queryByText('Federal Tax')).toBeNull();
    });

    it('is a no-op when the row is already in the asked-for state', async () => {
        // ArrowLeft on a collapsed row must NOT preventDefault — the key stays
        // the browser's. Asserting the state is unchanged and, more to the
        // point, that nothing else fired.
        const user = userEvent.setup();
        const btn = await openRegister();
        await user.click(btn.closest('[role="row"]')!);

        await user.keyboard('{ArrowLeft}');

        expect(btn).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByText('Federal Tax')).toBeNull();
        expect(screen.queryByLabelText('Posting 1 amount')).toBeNull();
    });

    it('yields to a focused toggle rather than acting from the row as well', async () => {
        // Same guard as the Enter branch. A row carries buttons, and arrows are
        // how a focused control moves between its own options, so the row
        // handler must not also fire.
        //
        // The visible consequence is that the arrows are dead while focus sits
        // ON the toggle — accepted, because the toggle answers Enter and Space
        // there, and the alternative is a row handler racing whatever control
        // has focus.
        const user = userEvent.setup();
        const btn = await openRegister();

        // BOTH steps matter. Clicking the row first sets the hook's focused-row
        // id; only then does moving DOM focus to the toggle isolate the BUTTON
        // guard as the thing under test.
        //
        // Without the click this test passed with the guard deleted, because
        // the handler bailed on a null focused row instead — green for a reason
        // that had nothing to do with what it claimed to check.
        await user.click(btn.closest('[role="row"]')!);
        btn.focus();

        await user.keyboard('{ArrowRight}');

        expect(btn).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByText('Federal Tax')).toBeNull();
    });
});

describe('the expand toggle does not leak its click to the row', () => {
    it('double-clicking the toggle expands, and does NOT open the editor behind it', async () => {
        // The row handles click (focus) and DOUBLE-click (open the editor).
        // The investment strategy has always stopped propagation on its
        // toggle; the bank one did not, so an impatient double-click both
        // expanded the split and dropped an editor on top of it.
        const user = userEvent.setup();
        const btn = await openRegister();

        await user.dblClick(btn);

        expect(screen.queryByLabelText('Posting 1 amount')).toBeNull();
        // ...and the toggle still did its own job. A double-click is two
        // clicks, so it toggles twice and lands back closed.
        expect(btn).toHaveAttribute('aria-expanded', 'false');
    });

    it('a single click expands without opening anything', async () => {
        const user = userEvent.setup();
        const btn = await openRegister();
        await user.click(btn);
        await waitFor(() => expect(btn).toHaveAttribute('aria-expanded', 'true'));
        expect(screen.queryByLabelText('Posting 1 amount')).toBeNull();
    });
});

describe('the split row menu', () => {
    it('offers Edit, so the split is reachable without a double-click', async () => {
        const btn = await openRegister();
        const row = btn.closest('[role="row"]')!;

        // fireEvent, not userEvent.pointer: the row listens for the DOM
        // `contextmenu` event, which userEvent's MouseRight does not emit.
        fireEvent.contextMenu(row);

        const menu = await screen.findByRole('menu');
        const edit = within(menu).getByRole('menuitem', { name: 'Edit' });
        // The shortcut hint is shown but aria-hidden, so the accessible name
        // stays "Edit" while the eye still gets "Enter". Asserting the hint
        // because it is only honest now that Enter actually opens the editor.
        expect(edit).toHaveTextContent('Enter');
    });
});
