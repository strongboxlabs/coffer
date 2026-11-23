import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { CategoryRows } from './CategoryRows';
import * as apiModule from '@/lib/api';
import type { BudgetCategoryRow, BudgetTransactionPage } from '@/lib/types';

// A budget row is a ROLLUP: its figure already contains every descendant. So
// the register it opens has to be a rollup too — scoped to the category AND its
// sub-categories — or the number you clicked is not the number you land on.
// That is one `search` prop on one Link, which is exactly the kind of thing
// that gets dropped in a refactor and noticed months later, so it is pinned
// here rather than left to a reader's eye.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';
const PARENT_ID = '00000000-0000-0000-0000-000000000200';
const MONTH = '2026-04';

const CHILD_ID = '00000000-0000-0000-0000-000000000201';

const ROW: BudgetCategoryRow = {
    categoryId: PARENT_ID,
    name: 'Food',
    parentId: null,
    actual: 120,
    typical: 100,
    target: null,
    targetCurrencyCode: null,
    mark: 100,
};
// A child, so the parent is a ROLLUP — which is the only shape where any of
// this matters: the per-line sub-category is shown only when there are
// sub-categories to distinguish.
const CHILD: BudgetCategoryRow = {
    categoryId: CHILD_ID,
    name: 'Groceries',
    parentId: PARENT_ID,
    actual: 120,
    typical: 100,
    target: null,
    targetCurrencyCode: null,
    mark: 100,
};

const PAGE: BudgetTransactionPage = {
    lines: [
        {
            headerId: '00000000-0000-0000-0000-0000000000a1',
            postedAt: '2026-04-03T12:00:00Z',
            payee: 'Market',
            accountId: '00000000-0000-0000-0000-000000000100',
            accountName: 'Checking',
            counterpartyAccountId: '00000000-0000-0000-0000-000000000201',
            counterpartyAccountName: 'Food/Groceries',
            amount: -120,
            memo: null,
            status: 'cleared',
            legId: '00000000-0000-0000-0000-0000000000b1',
            currencyCode: 'USD',
        },
    ],
    limit: 200,
    offset: 0,
    hasMore: false,
};

/** One split, two of its lines in the subtree: the same header and account, each
 *  line its own leg. The second is paid in another currency. */
const SPLIT_PAGE: BudgetTransactionPage = {
    ...PAGE,
    lines: [
        { ...PAGE.lines[0]!, payee: 'Payee A', amount: -70, legId: '00000000-0000-0000-0000-0000000000b2' },
        {
            ...PAGE.lines[0]!, payee: 'Payee B', amount: -50, legId: '00000000-0000-0000-0000-0000000000b3',
            currencyCode: 'EUR',
        },
    ],
};

function renderRows(
    rows: BudgetCategoryRow[] = [ROW, CHILD],
    detailId: string | null = PARENT_ID,
    onSetTarget: (categoryId: string, amount: number | null) => void = () => {},
) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });

    const root = createRootRoute();
    // The Link needs a matching route or TanStack throws.
    const registerRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: () => <main>register</main>,
    });
    const budgetRoute = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/budget',
        component: () => (
            <CategoryRows
                canEdit
                rows={rows}
                accountPaths={new Map([
                    [PARENT_ID, 'Food'],
                    [CHILD_ID, 'Food/Groceries'],
                ])}
                systemCategoryIds={new Set()}
                currency="USD"
                ledgerId={LEDGER_ID}
                monthKey={MONTH}
                expandedIds={new Set()}
                onToggle={() => {}}
                // Open on arrival — `detailId` is a prop, so the transactions
                // block renders without driving the row's gestures here.
                detailId={detailId}
                onToggleDetail={() => {}}
                onSetTarget={onSetTarget}
            />
        ),
    });
    const router = createRouter({
        routeTree: root.addChildren([budgetRoute, registerRoute]),
        history: createMemoryHistory({
            initialEntries: [`/ledgers/${LEDGER_ID}/budget`],
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

describe('CategoryRows — the register link', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('opens the register with the sub-category scope ON', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(PAGE);

        renderRows();

        const link = await screen.findByRole('link', { name: /open the register/i });
        const href = link.getAttribute('href') ?? '';
        expect(href).toContain(`/ledgers/${LEDGER_ID}/accounts/${PARENT_ID}`);
        // The whole point: a rollup row opens a rollup register.
        expect(href).toMatch(/subcategories=true/);
    });

    it('asks the server for this category and month', async () => {
        const spy = vi
            .spyOn(apiModule, 'fetchBudgetTransactions')
            .mockResolvedValue(PAGE);

        renderRows();

        // Anchor on the rendered line before asserting the call, so a pending
        // frame cannot pass this by accident.
        expect(await screen.findByText('Market')).toBeInTheDocument();
        expect(spy).toHaveBeenCalledWith(LEDGER_ID, PARENT_ID, MONTH, 200);
    });

    it('names the sub-category each line is filed under', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(PAGE);

        renderRows();

        // The list spans the SUBTREE, so a line has to say which descendant it
        // belongs to — by full path, like every category shown outside a tree.
        // Matched loosely: the label is rendered with a leading separator.
        const row = (await screen.findByText('Market')).closest('tr');
        expect(row).not.toBeNull();
        expect(
            within(row!).getByText((_, el) => el?.textContent === '· Food/Groceries'),
        ).toBeInTheDocument();
    });
});

describe('CategoryRows — a split\'s lines', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('lists each line of one split as its own row, named by its leg', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(SPLIT_PAGE);
        const errors = vi.spyOn(console, 'error').mockImplementation(() => {});

        renderRows();

        expect(await screen.findByText('Payee A')).toBeInTheDocument();
        expect(screen.getByText('Payee B')).toBeInTheDocument();
        // Keyed by header and account, the two lines collided: React warns about a
        // duplicate key and may drop or repeat a row.
        const duplicateKey = errors.mock.calls.some((args) => args.some((a) => /same key/.test(String(a))));
        expect(duplicateKey).toBe(false);
    });

    it('prints each line in its own currency', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(SPLIT_PAGE);

        renderRows();

        const eur = (await screen.findByText('Payee B')).closest('tr')!;
        expect(within(eur).getByText('€50.00')).toBeInTheDocument();
        const usd = screen.getByText('Payee A').closest('tr')!;
        expect(within(usd).getByText('$70.00')).toBeInTheDocument();
    });
});

describe('CategoryRows — lines in another currency (ADR-0114 D7, D9)', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });
    const MISSING = { fromCurrency: 'EUR', toCurrency: 'USD', date: '2026-04-03', reason: 'rate_unavailable' as const };

    it('says the other currency\'s lines are converted in the row\'s figure', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(SPLIT_PAGE);

        renderRows();

        expect(await screen.findByText(/^2 transactions this month\. The € lines are converted to USD at each one's date in the row's figure\./))
            .toBeInTheDocument();
        expect(screen.queryByText(/rate for this day/)).toBeNull();
    });

    it('says nothing about conversion when every line is in the reporting currency', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(PAGE);

        renderRows();

        expect(await screen.findByText(/^1 transaction this month\./)).not.toHaveTextContent('converted');
    });

    it('marks the line whose day has no rate, the row "no rate", and draws the mark\'s tick alone', async () => {
        vi.spyOn(apiModule, 'fetchBudgetTransactions').mockResolvedValue(SPLIT_PAGE);

        renderRows([{ ...ROW, actual: null, rateUnavailable: [MISSING] }], PARENT_ID);

        const eur = (await screen.findByText(/^Payee B/)).closest('tr')!;
        expect(eur).toHaveTextContent('· no EUR to USD rate for this day');
        const usd = screen.getByText('Payee A').closest('tr')!;
        expect(usd).not.toHaveTextContent('rate for this day');

        const tr = screen.getByRole('button', { name: 'Food' }).closest('tr')!;
        const cells = within(tr).getAllByRole('cell');
        expect(within(cells[1]!).getByText('no rate')).toHaveAttribute('title', 'No EUR to USD rate for Apr 3, 2026.');
        expect(within(cells[3]!).getByText('no rate')).toBeInTheDocument();
        expect(cells[4]!.querySelector('.bg-border-strong')).not.toBeNull();
        expect(cells[4]!.querySelector('.bg-accent')).toBeNull();
        expect(cells[4]!.querySelector('.bg-state-danger')).toBeNull();
    });

    // A target typed in another currency with no rate arrives as target null, mark
    // null and the pair named, and its own currency says it is stored (ADR-0114 D9,
    // D12): it must not read as unset, and opening the cell and leaving it must not
    // delete what is stored.
    const unconverted: BudgetCategoryRow = {
        categoryId: PARENT_ID, name: 'Food', parentId: null,
        actual: 40, typical: 100, target: null, targetCurrencyCode: 'EUR', mark: null,
        // A spend in a third currency is missing a rate too: the target cell names
        // only the target's own pair.
        rateUnavailable: [{ ...MISSING, fromCurrency: 'GBP' }, { ...MISSING, date: '2026-04-30' }],
    };

    it('marks a stored target with no rate "no rate" with its pair, and opening and leaving it deletes nothing', async () => {
        const user = userEvent.setup();
        const onSetTarget = vi.fn();
        renderRows([unconverted], null, onSetTarget);

        const tr = (await screen.findByRole('button', { name: 'Food' })).closest('tr')!;
        const target = within(tr).getAllByRole('cell')[2]!;
        const cell = within(target).getByRole('button');
        // Solid, as a typed target is, never the muted italic of the usual.
        expect(cell).not.toHaveClass('italic');
        expect(cell).toHaveAttribute('title', expect.stringContaining('Typed in EUR'));
        expect(within(target).getByText('no rate'))
            .toHaveAttribute('title', 'No EUR to USD rate for Apr 30, 2026.');

        await user.click(cell);
        await user.click(screen.getByRole('button', { name: 'Food' }));

        expect(screen.queryByLabelText('Target for Food')).toBeNull();
        expect(onSetTarget).not.toHaveBeenCalled();
    });

    it('clears a stored target with no rate on Enter, as clearing any target does', async () => {
        const user = userEvent.setup();
        const onSetTarget = vi.fn();
        renderRows([unconverted], null, onSetTarget);

        const tr = (await screen.findByRole('button', { name: 'Food' })).closest('tr')!;
        await user.click(within(within(tr).getAllByRole('cell')[2]!).getByRole('button'));
        expect(screen.getByLabelText('Target for Food')).toHaveValue(null);
        await user.keyboard('{Enter}');

        expect(onSetTarget).toHaveBeenCalledExactlyOnceWith(PARENT_ID, null);
    });

    it('sends no clear for a row whose mark has no rate but that stores no target', async () => {
        const user = userEvent.setup();
        const onSetTarget = vi.fn();
        renderRows([{ ...unconverted, targetCurrencyCode: null }], null, onSetTarget);

        const tr = (await screen.findByRole('button', { name: 'Food' })).closest('tr')!;
        const cell = within(within(tr).getAllByRole('cell')[2]!).getByRole('button');
        expect(cell).toHaveClass('italic');
        await user.click(cell);
        await user.keyboard('{Enter}');

        expect(onSetTarget).not.toHaveBeenCalled();
    });

    it('still clears a target it showed when the field is emptied', async () => {
        const user = userEvent.setup();
        const onSetTarget = vi.fn();
        renderRows([{ ...ROW, target: 90, mark: 90 }], null, onSetTarget);

        const tr = (await screen.findByRole('button', { name: 'Food' })).closest('tr')!;
        await user.click(within(tr).getByTitle('Your target. Clear it to go back to the usual.'));
        await user.clear(screen.getByLabelText('Target for Food'));
        await user.keyboard('{Enter}');

        expect(onSetTarget).toHaveBeenCalledWith(PARENT_ID, null);
    });
});

describe('CategoryRows — over is over', () => {
    const root = (categoryId: string, name: string, actual: number, mark: number | null): BudgetCategoryRow =>
        ({ categoryId, name, parentId: null, actual, typical: mark, target: null, targetCurrencyCode: null, mark });

    /** A row's Difference cell and its bar's overflow, by the row's name. */
    const cellsOf = async (name: string) => {
        const tr = (await screen.findByRole('button', { name })).closest('tr')!;
        const cells = within(tr).getAllByRole('cell');
        return { difference: cells[3]!, overflow: cells[4]!.querySelector('.bg-state-danger') };
    };

    it('prints any amount past the mark as over, "—" only at it, and the shortfall under it', async () => {
        renderRows([
            root('c-over', 'Over A', 100.4, 100),
            root('c-at', 'At A', 100, 100),
            root('c-under', 'Under A', 99.6, 100),
            // A mean finer than a printed figure, against spend printing the same figure.
            root('c-mean', 'Mean A', 33.33, 100 / 3),
            root('c-new', 'New A', 40, null),
        ], null);

        const over = await cellsOf('Over A');
        expect(over.difference).toHaveTextContent('+$0.40');
        expect(over.difference).toHaveClass('text-state-danger');
        // The overflow is drawn however little it is, wide enough to show past the tick.
        expect(over.overflow).not.toBeNull();
        expect(over.overflow).toHaveClass('min-w-fixed-6px');

        const at = await cellsOf('At A');
        expect(at.difference).toHaveTextContent('—');
        expect(at.difference).not.toHaveClass('text-state-danger');
        expect(at.overflow).toBeNull();

        const under = await cellsOf('Under A');
        expect(under.difference).toHaveTextContent('−$0.40');
        expect(under.difference).toHaveClass('text-text-muted');
        expect(under.overflow).toBeNull();

        const mean = await cellsOf('Mean A');
        expect(mean.difference).toHaveTextContent('—');
        expect(mean.overflow).toBeNull();

        expect((await cellsOf('New A')).difference).toHaveTextContent('new');
    });
});

// ADR-0023 §L: the target field gives way to the cell's button on Enter or
// Escape, taking focus with it; the button takes it. A blur has already put
// focus somewhere else, and it stays there.
describe('CategoryRows — the target cell hands focus on', () => {
    const root = (categoryId: string, name: string): BudgetCategoryRow =>
        ({ categoryId, name, parentId: null, actual: 10, typical: 20, target: null, targetCurrencyCode: null, mark: 20 });
    const targetOf = async (name: string) =>
        within((await screen.findByRole('button', { name })).closest('tr')!).getByTitle('Set a target');

    it('to the cell\'s button after Enter', async () => {
        const user = userEvent.setup();
        renderRows([root('c-a', 'Row A'), root('c-b', 'Row B')], null);

        await user.click(await targetOf('Row A'));
        await user.type(screen.getByLabelText('Target for Row A'), '5{Enter}');

        expect(screen.queryByLabelText('Target for Row A')).toBeNull();
        expect(await targetOf('Row A')).toHaveFocus();
    });

    it('to the field when the cell opens, and back to the cell\'s button after Escape', async () => {
        const user = userEvent.setup();
        renderRows([root('c-a', 'Row A'), root('c-b', 'Row B')], null);

        await user.click(await targetOf('Row A'));
        expect(screen.getByLabelText('Target for Row A')).toHaveFocus();
        await user.keyboard('{Escape}');

        expect(screen.queryByLabelText('Target for Row A')).toBeNull();
        expect(await targetOf('Row A')).toHaveFocus();
    });

    it('nowhere after a blur: focus stays where it went', async () => {
        const user = userEvent.setup();
        renderRows([root('c-a', 'Row A'), root('c-b', 'Row B')], null);

        await user.click(await targetOf('Row A'));
        await user.click(screen.getByRole('button', { name: 'Row B' }));

        expect(screen.queryByLabelText('Target for Row A')).toBeNull();
        expect(screen.getByRole('button', { name: 'Row B' })).toHaveFocus();
    });
});
