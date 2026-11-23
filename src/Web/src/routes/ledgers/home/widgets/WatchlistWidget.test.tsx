import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { StubbedCall } from '@/lib/annotations/fetchStub';
import { formatLedgerDate } from '@/lib/dates';
import type { BudgetCategoryRow } from '@/lib/types';

import { account, CHECKING, homeStub, layoutWith, LEDGER, renderHome } from '../homeTestFixtures';
import type { WidgetSettings } from '../dashboardLayout';

// The Watchlist through the real page, API client and router, with the server
// mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const ACCOUNTS = [
    account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
    account({ id: 'cat-a', name: 'Category A', accountType: 'category', categoryKind: 'expense' }),
    account({ id: 'cat-b', name: 'Category B', accountType: 'category', categoryKind: 'expense' }),
    account({ id: 'cat-b1', name: 'Child B', accountType: 'category', categoryKind: 'expense', parentId: 'cat-b' }),
    account({ id: 'cat-c', name: 'Category C', accountType: 'category', categoryKind: 'expense' }),
    account({ id: 'cat-d', name: 'Category D', accountType: 'category', categoryKind: 'expense' }),
    account({ id: 'cat-inc', name: 'Income A', accountType: 'category', categoryKind: 'income' }),
    account({ id: 'cat-old', name: 'Category Old', accountType: 'category', categoryKind: 'expense', isActive: false }),
];

const row = (over: Partial<BudgetCategoryRow> & Pick<BudgetCategoryRow, 'categoryId' | 'name'>): BudgetCategoryRow =>
    ({ parentId: null, actual: 0, typical: null, target: null, targetCurrencyCode: null, mark: null, ...over });

/** Over a typed target; under a typical one; spending with no mark yet. */
const ROWS = [
    row({ categoryId: 'cat-a', name: 'Category A', actual: 120, typical: 90, target: 100, mark: 100 }),
    row({ categoryId: 'cat-b1', name: 'Child B', parentId: 'cat-b', actual: 30, typical: 50, mark: 50 }),
    row({ categoryId: 'cat-c', name: 'Category C', actual: 40 }),
];

const shown = (settings: WidgetSettings, more: Parameters<typeof layoutWith>[0] = {}) =>
    layoutWith({ watchlist: { visible: true, settings }, ...more });

const panel = () => screen.findByRole('heading', { level: 2, name: /^Watchlist/ })
    .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

/** A watched row's link, found by the path it prints. */
const rowOf = async (path: string) =>
    (await within(await panel()).findByText(path)).closest('a') as HTMLAnchorElement;

const bar = (el: HTMLElement) => el.querySelector<HTMLElement>('span[aria-hidden].h-control-20px');

describe('Watchlist: a young ledger\'s typical', () => {
    it('names the day its history starts, where no one count of months is true', async () => {
        const FROM = '2026-01-15';
        homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-b1'] }), budget: { rows: ROWS, actualTotal: 190, historyFrom: FROM } });
        renderHome();
        const under = await rowOf('Category B/Child B');
        expect(under).toHaveTextContent(`typical, since ${formatLedgerDate(FROM)}`);
        expect(under).not.toHaveTextContent('3 months');
    });
});

describe('Watchlist', () => {
    it('draws each chosen category against its mark, in the order chosen', async () => {
        homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-b1', 'cat-a', 'cat-c'] }), budget: { rows: ROWS, actualTotal: 190 } });
        renderHome();

        const over = await rowOf('Category A');
        expect(within(await panel()).getByRole('heading', { level: 2 })).toHaveTextContent('Watchlist · Mar');
        expect(over).toHaveAttribute('href', `/ledgers/${LEDGER}/accounts/cat-a?subcategories=true`);
        expect(over).toHaveTextContent('$120.00 of $100.00');
        expect(within(over).getByText('budgeted', { exact: false })).toBeInTheDocument();
        expect(within(over).getByText('+$20.00 over')).toHaveClass('text-state-danger');
        expect(bar(over)!.querySelector('.bg-state-danger')).not.toBeNull();

        // A sub-category prints its path, whole on hover; its mark is typical.
        const under = await rowOf('Category B/Child B');
        expect(within(under).getByTitle('Category B/Child B')).toBeInTheDocument();
        expect(under).toHaveTextContent('$30.00 of $50.00');
        expect(under).toHaveTextContent('typical, 3 months');
        expect(bar(under)!.querySelector('.bg-state-danger')).toBeNull();
        expect(within(under).queryByText(/over$/)).toBeNull();

        const links = within(screen.getByRole('list', { name: 'Watched categories' })).getAllByRole('link');
        expect(links.map((l) => l.getAttribute('href'))).toEqual([
            `/ledgers/${LEDGER}/accounts/cat-b1?subcategories=true`,
            `/ledgers/${LEDGER}/accounts/cat-a?subcategories=true`,
            `/ledgers/${LEDGER}/accounts/cat-c?subcategories=true`,
        ]);
    });

    it('says when a watched category has nothing spent yet, and when it has no mark yet', async () => {
        homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-c', 'cat-d'] }), budget: { rows: ROWS, actualTotal: 190 } });
        renderHome();

        const none = await rowOf('Category D');
        expect(within(none).getByText('nothing spent yet')).toBeInTheDocument();
        expect(bar(none)).toBeNull();

        const fresh = await rowOf('Category C');
        expect(fresh).toHaveTextContent('$40.00 new');
        // A run and no tick: there is no mark to draw.
        expect(bar(fresh)!.querySelector('.bg-accent')).not.toBeNull();
        expect(bar(fresh)!.querySelector('.bg-border-strong')).toBeNull();
        expect(within(fresh).queryByText(/budgeted|typical/)).toBeNull();
    });

    it('says a parent is budgeted when its mark carries a target typed on a child', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-b', 'cat-c'] }),
            budget: {
                rows: [
                    row({ categoryId: 'cat-b', name: 'Category B', actual: 60, typical: 50, mark: 80 }),
                    row({ categoryId: 'cat-b1', name: 'Child B', parentId: 'cat-b', actual: 25, typical: 20, target: 30, mark: 30 }),
                    row({ categoryId: 'cat-c', name: 'Category C', actual: 40, typical: 45, mark: 45 }),
                ],
                actualTotal: 100,
            },
        });
        renderHome();

        const parent = await rowOf('Category B');
        expect(parent).toHaveTextContent('$60.00 of $80.00');
        expect(parent).toHaveTextContent('budgeted');
        expect(parent).not.toHaveTextContent('typical');
        // Nothing typed on it or beneath it: its mark is its typical.
        expect(await rowOf('Category C')).toHaveTextContent('typical, 3 months');
    });

    it('finds a target typed two levels down through a category the month leaves out', async () => {
        const grandchild = account({ id: 'cat-b1a', name: 'Grandchild B', accountType: 'category', categoryKind: 'expense', parentId: 'cat-b1' });
        homeStub({
            accounts: [...ACCOUNTS, grandchild],
            prefs: shown({ categoryIds: ['cat-b'] }),
            budget: {
                rows: [
                    row({ categoryId: 'cat-b', name: 'Category B', actual: 60, typical: 50, mark: 80 }),
                    row({ categoryId: 'cat-b1a', name: 'Grandchild B', parentId: 'cat-b1', target: 30, mark: 30 }),
                ],
                actualTotal: 60,
            },
        });
        renderHome();

        const parent = await rowOf('Category B');
        expect(parent).toHaveTextContent('budgeted');
        expect(parent).not.toHaveTextContent('typical');
    });

    it('counts a category over by less than a whole unit as over: over is over', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-a'] }),
            budget: { rows: [row({ categoryId: 'cat-a', name: 'Category A', actual: 100.4, typical: 100, target: 100, mark: 100 })], actualTotal: 100.4 },
        });
        renderHome();

        const over = await rowOf('Category A');
        expect(within(over).getByText('+$0.40 over')).toHaveClass('text-state-danger');
        expect(bar(over)!.querySelector('.bg-state-danger')).toHaveClass('min-w-fixed-6px');
    });

    it('leaves out a chosen id that names no category', async () => {
        homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-gone', 'cat-a'] }), budget: { rows: ROWS, actualTotal: 190 } });
        renderHome();

        expect(await rowOf('Category A')).toBeInTheDocument();
        expect(within(screen.getByRole('list', { name: 'Watched categories' })).getAllByRole('link')).toHaveLength(1);
    });

    it('sorts most over first, by how far past the mark, and a category with no mark last', async () => {
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-c', 'cat-b1', 'cat-d', 'cat-a'], sort: 'most-over' }),
            budget: { rows: [...ROWS, row({ categoryId: 'cat-d', name: 'Category D', actual: 10, typical: 60, mark: 60 })], actualTotal: 200 },
        });
        renderHome();

        await rowOf('Category A');
        const links = within(screen.getByRole('list', { name: 'Watched categories' })).getAllByRole('link');
        expect(links.map((l) => l.getAttribute('href')!.split('/accounts/')[1]!.split('?')[0])).toEqual(['cat-a', 'cat-b1', 'cat-d', 'cat-c']);
    });

    it('keeps a row\'s figures when only the month\'s total needs a missing rate', async () => {
        const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-02', reason: 'rate_unavailable' as const };
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-a'] }),
            budget: { rows: ROWS, actualTotal: null, rateUnavailable: [missing] },
        });
        renderHome();

        const over = await rowOf('Category A');
        expect(over).toHaveTextContent('$120.00 of $100.00');
        expect(within(await panel()).queryByText('mixed')).toBeNull();
    });

    it('reads a row whose spend needs a missing rate "no rate", not "nothing spent yet", and draws no bar', async () => {
        const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-08', reason: 'rate_unavailable' as const };
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-a', 'cat-c'] }),
            budget: {
                rows: [
                    row({ categoryId: 'cat-a', name: 'Category A', actual: null, typical: 90, target: 100, mark: 100, rateUnavailable: [missing] }),
                    row({ categoryId: 'cat-c', name: 'Category C', actual: 40 }),
                ],
                actualTotal: null,
                rateUnavailable: [missing],
            },
        });
        renderHome();

        const unconverted = await rowOf('Category A');
        expect(within(unconverted).getByText('no rate')).toHaveAttribute('title', 'No CAD to USD rate for Mar 8, 2026.');
        expect(unconverted).toHaveTextContent('of $100.00');
        expect(unconverted).not.toHaveTextContent('nothing spent yet');
        expect(unconverted).not.toHaveTextContent('over');
        expect(bar(unconverted)).toBeNull();
        // A row with its rates still prints.
        expect(await rowOf('Category C')).toHaveTextContent('$40.00 new');
    });

    it('reads a mark that needs a missing rate "no rate", not "new"', async () => {
        // A target typed in EUR, with no EUR to USD rate for the month: spend is known,
        // the mark is not.
        const missing = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-03-12', reason: 'rate_unavailable' as const };
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-a', 'cat-c'] }),
            budget: {
                rows: [
                    row({ categoryId: 'cat-a', name: 'Category A', actual: 70, typical: 90, target: null, mark: null, rateUnavailable: [missing] }),
                    row({ categoryId: 'cat-c', name: 'Category C', actual: 40 }),
                ],
            },
        });
        renderHome();

        const unconverted = await rowOf('Category A');
        expect(unconverted).toHaveTextContent('$70.00');
        expect(within(unconverted).getByText('no rate')).toHaveAttribute('title', 'No EUR to USD rate for Mar 12, 2026.');
        expect(unconverted).not.toHaveTextContent('new');
        expect(await rowOf('Category C')).toHaveTextContent('$40.00 new');
    });

    it('sorts a row with no rate after the ones with a distance from their mark', async () => {
        const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-08', reason: 'rate_unavailable' as const };
        homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-d', 'cat-a'], sort: 'most-over' }),
            budget: {
                rows: [
                    row({ categoryId: 'cat-a', name: 'Category A', actual: 120, typical: 90, target: 100, mark: 100 }),
                    row({ categoryId: 'cat-d', name: 'Category D', actual: null, typical: 60, mark: 60, rateUnavailable: [missing] }),
                ],
                actualTotal: null,
            },
        });
        renderHome();

        await rowOf('Category A');
        const links = within(screen.getByRole('list', { name: 'Watched categories' })).getAllByRole('link');
        expect(links.map((l) => l.getAttribute('href')!.split('/accounts/')[1]!.split('?')[0])).toEqual(['cat-a', 'cat-d']);
    });

    it('reads This month\'s window from the same request This month makes', async () => {
        const stub = homeStub({
            accounts: ACCOUNTS,
            prefs: shown({ categoryIds: ['cat-a'] }, { 'budget-pace': { settings: { windowMonths: 6 } } }),
            budget: { rows: ROWS, actualTotal: 190, windowMonths: 6 },
        });
        renderHome();

        expect(await rowOf('Category A')).toBeInTheDocument();
        expect(await screen.findByRole('heading', { level: 2, name: /^This month · day/ })).toBeInTheDocument();
        const calls = stub.callsTo('GET', /\/budget\/progress$/);
        expect(calls).toHaveLength(1);
        expect(calls[0]!.search.get('window')).toBe('6');
    });

    it('says when no category is chosen, and opens its settings from there', async () => {
        homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-gone'] }), budget: { rows: ROWS, actualTotal: 190 } });
        renderHome();
        const user = userEvent.setup({ delay: null });

        expect(await within(await panel()).findByText('No categories chosen')).toBeInTheDocument();
        await user.click(within(await panel()).getByRole('button', { name: 'Settings…' }));
        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });
        // The id it held names nothing, so nothing is chosen and there is nothing to keep.
        expect(await within(settings).findByText('0 of 8')).toBeInTheDocument();
        expect(within(settings).getByRole('button', { name: 'Save' })).toBeDisabled();
    });
});

describe('Watchlist settings', () => {
    const openSettings = async () => {
        const user = userEvent.setup({ delay: null });
        await user.click(await screen.findByRole('button', { name: 'Watchlist actions' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Settings…' }));
        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });
        return { user, settings, categories: within(settings).getByRole('group', { name: 'Categories' }) };
    };

    it('lists the chosen categories, drops an id that names none, and adds an active expense category', async () => {
        const stub = homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-a', 'cat-gone', 'cat-b1'] }), budget: { rows: ROWS, actualTotal: 190 } });
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        renderHome();
        const { user, settings, categories } = await openSettings();

        const list = await within(categories).findByRole('list', { name: 'Chosen categories' });
        expect(within(list).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Category A', 'Category B/Child B']);
        expect(within(categories).getByText('2 of 8')).toBeInTheDocument();

        // The picker offers the active expense categories not chosen yet.
        await user.click(within(categories).getByRole('combobox', { name: 'Add a category' }));
        const options = await screen.findAllByRole('option');
        expect(options.map((o) => o.textContent)).toEqual(['Category B', 'Category C', 'Category D']);
        fireEvent.mouseDown(options[1]!);

        expect(await within(categories).findByText('3 of 8')).toBeInTheDocument();
        await user.click(within(settings).getByRole('button', { name: 'Save' }));
        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        const body = stub.callsTo('PUT', /\/preferences\/dashboard$/)[0]!.body as { widgets: { key: string; settings: WidgetSettings }[] };
        expect(body.widgets.find((w) => w.key === 'watchlist')!.settings.categoryIds).toEqual(['cat-a', 'cat-b1', 'cat-c']);
    });

    it('removes a category, and keeps nothing once none is chosen', async () => {
        homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-a'] }), budget: { rows: ROWS, actualTotal: 190 } });
        renderHome();
        const { user, settings, categories } = await openSettings();

        const save = within(settings).getByRole('button', { name: 'Save' });
        await within(categories).findByRole('list', { name: 'Chosen categories' });
        expect(save).toBeEnabled();
        await user.click(within(categories).getByRole('button', { name: 'Remove Category A' }));

        expect(within(categories).getByText('0 of 8')).toBeInTheDocument();
        expect(within(categories).queryByRole('list', { name: 'Chosen categories' })).toBeNull();
        expect(save).toBeDisabled();
    });

    it('takes no more than eight', async () => {
        const many = Array.from({ length: 8 }, (_, i) =>
            account({ id: `cat-${i}`, name: `Category ${i}`, accountType: 'category', categoryKind: 'expense' }));
        homeStub({ accounts: [...ACCOUNTS, ...many], prefs: shown({ categoryIds: many.map((a) => a.id) }), budget: { rows: ROWS, actualTotal: 190 } });
        renderHome();
        const { categories } = await openSettings();

        expect(await within(categories).findByText('8 of 8')).toBeInTheDocument();
        expect(within(categories).getByRole('combobox', { name: 'Add a category' })).toBeDisabled();
    });

    describe('focus as the list changes', () => {
        // ADR-0023 §L: a row removed by its own button hands focus to the next
        // row's, else the previous row's, else (the picker opening its list on
        // focus) the settings panel; the add that disables the picker gives
        // focus to the new row's Remove.
        const removeFromKeyboard = async (user: ReturnType<typeof userEvent.setup>, categories: HTMLElement, name: string) => {
            within(categories).getByRole('button', { name }).focus();
            await user.keyboard('{Enter}');
            expect(within(categories).queryByRole('button', { name })).toBeNull();
        };

        it('gives focus to the next row\'s Remove from the middle, the previous row\'s from the end', async () => {
            homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-a', 'cat-c', 'cat-d'] }), budget: { rows: ROWS, actualTotal: 190 } });
            renderHome();
            const { user, categories } = await openSettings();
            await within(categories).findByRole('list', { name: 'Chosen categories' });

            await removeFromKeyboard(user, categories, 'Remove Category C');
            expect(within(categories).getByRole('button', { name: 'Remove Category D' })).toHaveFocus();

            await removeFromKeyboard(user, categories, 'Remove Category D');
            expect(within(categories).getByRole('button', { name: 'Remove Category A' })).toHaveFocus();
        });

        it('gives focus to the settings panel when the only row goes, and Escape still cancels it', async () => {
            homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-a'] }), budget: { rows: ROWS, actualTotal: 190 } });
            renderHome();
            const { user, settings, categories } = await openSettings();
            await within(categories).findByRole('list', { name: 'Chosen categories' });

            await removeFromKeyboard(user, categories, 'Remove Category A');

            expect(settings).toHaveFocus();
            await user.keyboard('{Escape}');
            expect(screen.queryByRole('group', { name: 'Watchlist settings' })).toBeNull();
        });

        it('gives focus to the new row\'s Remove when the add fills the list', async () => {
            const seven = Array.from({ length: 7 }, (_, i) =>
                account({ id: `cat-${i}`, name: `Category ${i}`, accountType: 'category', categoryKind: 'expense' }));
            homeStub({ accounts: [...ACCOUNTS, ...seven], prefs: shown({ categoryIds: seven.map((a) => a.id) }), budget: { rows: ROWS, actualTotal: 190 } });
            renderHome();
            const { user, categories } = await openSettings();
            expect(await within(categories).findByText('7 of 8')).toBeInTheDocument();

            const picker = within(categories).getByRole('combobox', { name: 'Add a category' });
            await user.click(picker);
            await user.keyboard('Category D');
            fireEvent.mouseDown((await screen.findAllByRole('option')).find((o) => o.textContent === 'Category D')!);

            expect(await within(categories).findByText('8 of 8')).toBeInTheDocument();
            expect(picker).toBeDisabled();
            expect(within(categories).getByRole('button', { name: 'Remove Category D' })).toHaveFocus();
        });

        it('leaves focus in the picker for an add below the cap', async () => {
            homeStub({ accounts: ACCOUNTS, prefs: shown({ categoryIds: ['cat-a'] }), budget: { rows: ROWS, actualTotal: 190 } });
            renderHome();
            const { user, categories } = await openSettings();
            await within(categories).findByRole('list', { name: 'Chosen categories' });

            const picker = within(categories).getByRole('combobox', { name: 'Add a category' });
            await user.click(picker);
            fireEvent.mouseDown((await screen.findAllByRole('option')).find((o) => o.textContent === 'Category C')!);

            expect(await within(categories).findByText('2 of 8')).toBeInTheDocument();
            expect(picker).toHaveFocus();
        });
    });
});
