import { beforeEach, describe, expect, it } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { CategoriesPanel } from './CategoriesPanel';
import type { CategoryNode } from '@/lib/types';

// The panel had NO visible controls — `grep -c '<Button|<IconButton'` returned
// zero — so Add sub-category / Rename / Move / Merge / Delete were reachable
// only by right-clicking a row, advertised by a title tooltip that does not
// exist on touch and is not announced as an affordance.
//
// These cover the visible path and the one branch that is easy to get wrong:
// SYSTEM categories have no actions, so they must not get a button — and must
// still line up with the rows that do.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function node(over: Partial<CategoryNode> & { id: string; name: string }): CategoryNode {
    return {
        parentId: null,
        isSystem: false,
        isActive: true,
        categoryKind: 'expense',
        transactionCount: 0,
        childCount: 0,
        total: 0,
        ...over,
    } as CategoryNode;
}

/**
 * Render the panel with its sections EXPANDED unless a test says otherwise.
 *
 * Sections now default to collapsed, and a collapsed body is `hidden`, which
 * takes it out of the accessibility tree — so every test about a row would
 * otherwise fail on a query that cannot see it. Seeding the stored preference
 * is closer to the real case anyway: a user looking at their categories has
 * opened the section. Pass `{ pristine: true }` to test the default itself.
 */
function renderPanel(
    categories: CategoryNode[],
    { pristine = false }: { pristine?: boolean } = {},
) {
    if (!pristine) {
        for (const kind of ['income', 'expense', 'adjustment']) {
            try {
                window.localStorage.setItem(
                    `coffer.categories.${LEDGER_ID}.${kind}.expanded`, 'true');
            } catch { /* private mode — the test will read the default */ }
        }
    }
    const queryClient = new QueryClient({
        defaultOptions: {
            // `staleTime: Infinity` is load-bearing, not tidiness. Seeded data
            // is stale the moment it lands, so the panel refetches on mount;
            // there is no fetch in jsdom, the query errors, and the error state
            // REPLACES the sections. A fast assertion wins that race and a
            // slower one does not — which showed up as a 5s timeout in a test
            // that looped over three sections, and would have surfaced sooner
            // or later as a flake in any of them.
            queries: { retry: false, staleTime: Infinity },
            mutations: { retry: false },
        },
    });
    // The real key carries the showInactive flag as a third element; seeding
    // ['categories', id] alone leaves the panel loading forever.
    queryClient.setQueryData(['categories', LEDGER_ID, false], categories);
    queryClient.setQueryData(['accounts', LEDGER_ID], []);

    const root = createRootRoute();
    const home = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <CategoriesPanel ledgerId={LEDGER_ID} />,
    });
    const account = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/accounts/$accountId',
        component: () => <div>account</div>,
    });
    const router = createRouter({
        routeTree: root.addChildren([home, account]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });

    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('CategoriesPanel row actions', () => {
    it('gives an editable category a visible actions button', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        expect(await screen.findByRole('button', { name: 'Actions for Groceries' }))
            .toBeInTheDocument();
    });

    it('opens the action menu from the button', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        // Anchor first: absent before the click, so the assertion after cannot
        // pass on a menu that was always rendered.
        expect(screen.queryByText('Rename')).not.toBeInTheDocument();

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));

        expect(await screen.findByText('Rename')).toBeInTheDocument();
        expect(screen.getByText('Add sub-category')).toBeInTheDocument();
        expect(screen.getByText('Delete')).toBeInTheDocument();
    });

    it('gives a system category no actions button', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Opening Balance', isSystem: true }),
        ]);
        // Anchor on the row that SHOULD have one, so this cannot pass because
        // nothing rendered at all.
        expect(await screen.findByRole('button', { name: 'Actions for Groceries' }))
            .toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Actions for Opening Balance' }))
            .not.toBeInTheDocument();
    });
});

describe('the adjustment kind (mig 224)', () => {
    it('lists an adjustment category instead of silently dropping it', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'House Revaluation', categoryKind: 'adjustment' }),
        ]);

        // The defect: the panel rendered exactly two KindSections and filtered
        // each on `categoryKind === kind`, so this row was fetched from the API
        // and then dropped on the floor. Anchored on the expense row, which was
        // never in doubt, so this cannot pass because nothing rendered.
        expect(await screen.findByText('Groceries')).toBeInTheDocument();
        expect(screen.getByText('House Revaluation')).toBeInTheDocument();
    });

    it('offers the section even when the ledger has no adjustment categories', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        // Deliberate: the per-section "New" buttons are the only create
        // affordance on this screen, so a section that appeared only once one
        // existed would leave no way to make the first one.
        expect(await screen.findByRole('button', { name: /New adjustment/i }))
            .toBeInTheDocument();
        expect(screen.getByText('No adjustment categories yet.')).toBeInTheDocument();
    });

    it('shows an adjustment total signed, not flipped like income', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Markup', categoryKind: 'adjustment', total: -4200 }),
            node({ id: 'c2', name: 'Salary', categoryKind: 'income', total: -4200 }),
        ]);

        // Same raw total, two kinds, two readings. Income negates so earnings
        // read positive under its header; an adjustment has no natural
        // direction and keeps its sign. The pair is the assertion — either
        // figure alone would pass against a function that treated both alike.
        await screen.findByText('Markup');
        const section = (label: string) =>
            screen.getByRole('heading', { name: new RegExp(label, 'i') })
                .closest('section')!;

        expect(section('Adjustment').textContent).toContain('-$4,200.00');
        expect(section('Income').textContent).toContain('$4,200.00');
        expect(section('Income').textContent).not.toContain('-$4,200.00');
    });
});

describe('sections collapse, because a real taxonomy is long', () => {
    // The demo alone has 78 expense categories, so anything below them is
    // ~2500px down. Adding a third section made that obvious; collapsing is
    // the general fix, not an Adjustment-specific one.
    //
    // The section headings are matched by the COUNT suffix. Their accessible
    // name is "Expense (1)", and a bare /Expense/i also matches "New expense"
    // — which `findBy*` reports as an ambiguous match and then retries until
    // it times out, so the symptom is a 5s hang rather than a useful error.
    // A FUNCTION matcher, not a regex built by template literal: `\(` and `\d`
    // inside a template literal are resolved by JS before RegExp ever sees
    // them, so the pattern silently became `^Expense (d+)$` and matched
    // nothing — which surfaced as a 5s timeout, not as a bad-pattern error.
    const section = (label: string) => (name: string) =>
        name.startsWith(label) && /\(\d+\)\s*$/.test(name);

    beforeEach(() => {
        try { window.localStorage.clear(); } catch { /* private mode */ }
    });

    it('hides a section body on toggle and keeps its total reachable', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Paycheck', categoryKind: 'income', total: -9200 }),
        ]);

        const expense = await screen.findByRole('button', { name: section('Expense') });
        expect(screen.getByText('Groceries')).toBeVisible();

        fireEvent.click(expense);

        expect(screen.getByText('Groceries')).not.toBeVisible();
        expect(expense).toHaveAttribute('aria-expanded', 'false');

        // The point of collapsing is to reach what is BELOW, so the header
        // must survive it — New included. A collapse that took the whole
        // section away would trade one scroll for a worse one.
        expect(screen.getByRole('button', { name: /New expense/i })).toBeVisible();
        expect(screen.getByText('Paycheck')).toBeVisible();
    });

    it('keeps New outside the disclosure, so creating cannot collapse', async () => {
        // A control that both creates a category and collapses the list it
        // lands in would be a trap. Asserted structurally rather than by
        // clicking New: the click opens a modal, and chasing the section
        // through that proves nothing about where the button lives.
        //
        // It is also an HTML validity check — a button inside a button is
        // invalid, and React renders it anyway.
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        const toggle = await screen.findByRole('button', { name: section('Expense') });
        const create = screen.getByRole('button', { name: /New expense/i });

        expect(toggle.contains(create)).toBe(false);
        expect(create.closest('[aria-expanded]')).toBeNull();
        // ...and they really are in the same section header, so this is not
        // passing because one of them rendered somewhere else entirely.
        expect(toggle.closest('section')).toBe(create.closest('section'));
    });

    it('starts every section collapsed on a ledger with no stored preference', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Paycheck', categoryKind: 'income', total: -9200 }),
        ], { pristine: true });

        for (const label of ['Income', 'Expense', 'Adjustment']) {
            expect(await screen.findByRole('button', { name: section(label) }))
                .toHaveAttribute('aria-expanded', 'false');
        }
        expect(screen.getByText('Groceries')).not.toBeVisible();

        // The totals survive the collapse — that is what makes the folded
        // screen a summary rather than three dead rows. Scoped to the section
        // HEADER: the same figure is also the Paycheck row's amount, hidden
        // below, and an unscoped text query matches both.
        const income = (await screen.findByRole('button', { name: section('Income') }))
            .closest('section')!;
        const header = income.firstElementChild!;
        expect(header).not.toHaveAttribute('hidden');
        expect(header.textContent).toContain('$9,200.00');
    });

    it('remembers an expanded section across a remount', async () => {
        // Pristine, so the toggle below is what puts the preference there —
        // otherwise the assertion could pass on the default alone.
        const first = renderPanel([node({ id: 'c1', name: 'Groceries' })], { pristine: true });
        fireEvent.click(await screen.findByRole('button', { name: section('Expense') }));
        expect(screen.getByText('Groceries')).toBeVisible();
        first.unmount();

        renderPanel([node({ id: 'c1', name: 'Groceries' })], { pristine: true });

        // Income is the control: untouched, so it must still read the default.
        expect(await screen.findByRole('button', { name: section('Expense') }))
            .toHaveAttribute('aria-expanded', 'true');
        expect(screen.getByRole('button', { name: section('Income') }))
            .toHaveAttribute('aria-expanded', 'false');
    });
});

