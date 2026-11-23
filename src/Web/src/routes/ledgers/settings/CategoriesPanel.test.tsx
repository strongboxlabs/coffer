import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, fireEvent, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { CategoriesPanel } from './CategoriesPanel';
import * as apiModule from '@/lib/api';
import { formatCurrency } from '@/lib/money';
import type { AccountSummary, CategoryList, CategoryNode } from '@/lib/types';
import {
    LEDGER_ID,
    renderInRouter,
    seedRole,
    testQueryClient,
} from '../structure/structureTestFixtures';

// The Categories tab of Structure (ADR-0111). Without visible controls, Add sub-category / Rename /
// Move / Merge / Delete would be reachable only by right-clicking a row, advertised by a title tooltip
// that does not exist on touch and is not announced as an affordance. These cover the visible path, the
// branches that are easy to get wrong (SYSTEM categories have no actions and must still line up with the
// rows that do; the Merge item must agree with the dialog about whether there is anything to merge
// into), the kind sections, search, and what a reader who cannot edit is shown.

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

/** The categories read: the rows, and the ledger's reporting currency their figures are in. */
function listOf(categories: CategoryNode[], currencyCode = 'USD'): CategoryList {
    return { categories, currencyCode };
}

/** The ledger's accounts read, which also carries the categories: active ones only, as the real read does. */
function accountsOf(categories: CategoryNode[]): AccountSummary[] {
    return categories
        .filter((c) => c.isActive)
        .map((c) => ({
            id: c.id, ledgerId: LEDGER_ID, parentId: c.parentId, name: c.name, accountType: 'category',
            categoryKind: c.categoryKind, currencyCode: 'USD', isActive: c.isActive, isSystem: c.isSystem,
            feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false,
            institutionName: null,
        }));
}

/**
 * Render the panel with its sections EXPANDED unless a test says otherwise.
 *
 * Sections default to collapsed, and a collapsed body is `hidden`, which
 * takes it out of the accessibility tree — so every test about a row would
 * otherwise fail on a query that cannot see it. Seeding the stored preference
 * is closer to the real case anyway: a user looking at their categories has
 * opened the section. Pass `{ pristine: true }` to test the default itself.
 */
function renderPanel(
    categories: CategoryNode[],
    { pristine = false, role = 'owner', withInactive, accounts, currencyCode = 'USD' }: {
        pristine?: boolean;
        role?: string;
        /** What the list returns with "Show inactive" on. */
        withInactive?: CategoryNode[];
        /** The ledger's accounts read, when a test needs its own. */
        accounts?: AccountSummary[];
        /** The ledger's reporting currency, which the categories read carries. */
        currencyCode?: string;
    } = {},
) {
    if (!pristine) {
        for (const kind of ['income', 'expense', 'adjustment']) {
            try {
                window.localStorage.setItem(
                    `coffer.categories.${LEDGER_ID}.${kind}.expanded`, 'true');
            } catch { /* private mode — the test will read the default */ }
        }
    }
    const queryClient = testQueryClient();
    // The real key carries the showInactive flag as a third element; seeding
    // ['categories', id] alone leaves the panel loading forever.
    queryClient.setQueryData(['categories', LEDGER_ID, false], listOf(categories, currencyCode));
    queryClient.setQueryData(
        ['categories', LEDGER_ID, true], listOf(withInactive ?? categories, currencyCode));
    queryClient.setQueryData(['accounts', LEDGER_ID], accounts ?? accountsOf(categories));
    // The reader's role, which decides whether any control is drawn at all.
    seedRole(queryClient, role);
    return { queryClient, ...renderInRouter(<CategoriesPanel ledgerId={LEDGER_ID} />, queryClient) };
}

beforeEach(() => {
    vi.restoreAllMocks();
    try { window.localStorage.clear(); } catch { /* private mode */ }
});

describe('CategoriesPanel row actions', () => {
    it('gives an editable category a visible actions button', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        expect(await screen.findByRole('button', { name: 'Actions for Groceries' }))
            .toBeInTheDocument();
    });

    it('opens the same menu from the button as from right-click', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' }), node({ id: 'c2', name: 'Dining' })]);

        // Anchor first: absent before the click, so the assertion after cannot
        // pass on a menu that was always rendered.
        expect(screen.queryByRole('menu')).not.toBeInTheDocument();

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        const fromButton = (await screen.findAllByRole('menuitem')).map((i) => i.textContent);
        expect(fromButton).toEqual([
            'Open register', 'Add sub-category', 'Rename…', 'Move…', 'Merge…', 'Deactivate', 'Delete',
        ]);
        fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' });
        await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());

        fireEvent.contextMenu(screen.getByText('Groceries'));
        const fromRightClick = (await screen.findAllByRole('menuitem')).map((i) => i.textContent);
        expect(fromRightClick).toEqual(fromButton);
    });

    it('opens the register from the menu', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Open register' }));
        expect(await screen.findByText('register page')).toBeInTheDocument();
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
        // ...and no menu on right-click either.
        fireEvent.contextMenu(screen.getByText('Opening Balance'));
        expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    });

    // The server refuses every write from a reader who cannot edit, so a control for one is a
    // button that can only fail. The viewer tests below pair with the owner and editor test:
    // the same ledger and the same rows, with the controls present for the roles that can edit.
    it('draws no row actions and no New category for a viewer', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })], { role: 'viewer' });
        expect(await screen.findByText('Groceries')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Actions for Groceries' })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /New category/ })).not.toBeInTheDocument();
        // Reading still works: the section header and the link into the register stay.
        expect(screen.getByRole('button', { name: /^Expense/ })).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Groceries' })).toBeInTheDocument();
    });

    it.each(['owner', 'editor'])('draws New category and row actions for an %s', async (role) => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })], { role });
        expect(await screen.findByRole('button', { name: 'Actions for Groceries' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'New category' })).toBeInTheDocument();
    });

    it('opens no menu on right-click for a viewer', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })], { role: 'viewer' });
        fireEvent.contextMenu(await screen.findByText('Groceries'));
        expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    });
});

describe('Merge in the row menu agrees with the merge dialog', () => {
    async function mergeItem(name: string) {
        fireEvent.click(await screen.findByRole('button', { name: `Actions for ${name}` }));
        return screen.findByRole('menuitem', { name: 'Merge…' });
    }

    it('is offered when another active category exists, of whatever kind', async () => {
        // The dialog's targets are not narrowed by kind (a cross-kind merge is how a category is
        // reclassified), so the menu must not be either: the old rule enabled Merge only for a
        // same-kind peer.
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Salary', categoryKind: 'income' }),
        ]);
        expect(await mergeItem('Groceries')).toBeEnabled();
    });

    it('is not offered when the only other category is inactive', async () => {
        // An inactive category is not an eligible target, so the dialog would have said "there is
        // no other category" — and the old menu, which counted inactive rows, offered it anyway.
        const groceries = node({ id: 'c1', name: 'Groceries' });
        const old = node({ id: 'c2', name: 'Old', isActive: false });
        renderPanel([groceries], { withInactive: [groceries, old] });
        await userEvent.click(await screen.findByLabelText('Show inactive'));
        await screen.findByText('Old');
        expect(await mergeItem('Groceries')).toBeDisabled();
    });

    it('is not offered when the only other category is beneath it', async () => {
        // The source's children move to the target, so a target beneath it would end up under itself.
        renderPanel([
            node({ id: 'c1', name: 'Auto', childCount: 1 }),
            node({ id: 'c2', name: 'Fuel', parentId: 'c1' }),
        ]);
        expect(await mergeItem('Auto')).toBeDisabled();
        // Fuel can merge up into Auto. A child's button is named by its path, which is what a
        // screen reader gets in place of the indentation.
        fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' });
        await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());
        expect(await mergeItem('Auto/Fuel')).toBeEnabled();
    });
});

describe('the merge dialog', () => {
    async function openMerge(name: string) {
        fireEvent.click(await screen.findByRole('button', { name: `Actions for ${name}` }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Merge…' }));
        return screen.findByRole('dialog');
    }

    it('says what it moves, budget targets included, once a destination is chosen and not before', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries', transactionCount: 4 }),
            node({ id: 'c2', name: 'Dining' }),
        ]);
        const dialog = await openMerge('Groceries');

        // Nothing is moving anywhere until there is somewhere to move to.
        expect(within(dialog).queryByText(/Budget targets move too/)).not.toBeInTheDocument();
        expect(within(dialog).getByRole('button', { name: 'Merge' })).toBeDisabled();

        await userEvent.click(within(dialog).getByRole('combobox'));
        await userEvent.click(await screen.findByRole('option', { name: 'Dining' }));

        expect(within(dialog).getByText('4 transactions')).toBeInTheDocument();
        // A number somebody typed moves with the category (ADR-0099), so the dialog says how.
        expect(within(dialog).getByText(/Budget targets move too/)).toHaveTextContent(
            /added together.*cannot both have a target.*the target on the lower category is dropped/);
        // And it does not promise what a merge cannot do.
        expect(within(dialog).getByText(/nothing moves back/)).toBeInTheDocument();
        expect(within(dialog).queryByText(/can be undone/)).not.toBeInTheDocument();
        expect(within(dialog).getByRole('button', { name: 'Merge' })).toBeEnabled();
    });

    it('sends the merge to the destination chosen', async () => {
        const merge = vi.spyOn(apiModule, 'mergeCategory').mockResolvedValue({
            transactionsMoved: 4, childrenReparented: 0, dryRun: false, budgetTargetsMoved: 2, budgetTargetsDropped: 0,
        });
        const { queryClient } = renderPanel([
            node({ id: 'c1', name: 'Groceries', transactionCount: 4 }),
            node({ id: 'c2', name: 'Dining' }),
        ]);
        const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
        const dialog = await openMerge('Groceries');
        await userEvent.click(within(dialog).getByRole('combobox'));
        await userEvent.click(await screen.findByRole('option', { name: 'Dining' }));
        await userEvent.click(within(dialog).getByRole('button', { name: 'Merge' }));

        await waitFor(() => expect(merge).toHaveBeenCalledWith(LEDGER_ID, 'c1', { targetId: 'c2' }));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        // Every category dialog's save goes through one onSaved: the home screen's
        // reads name categories, so they are refreshed with the tree (ADR-0079).
        const keys = invalidated.mock.calls.map((c) => c[0]?.queryKey);
        expect(keys).toContainEqual(['home', LEDGER_ID]);
        expect(keys).toContainEqual(['overview', LEDGER_ID]);
        expect(keys).toContainEqual(['budget-progress', LEDGER_ID]);
    });

    it('says in words which month and pair a missing rate stops the merge on, and links to the rates', async () => {
        const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-10-31', reason: 'rate_unavailable' as const };
        vi.spyOn(apiModule, 'mergeCategory').mockRejectedValue(
            new apiModule.ApiError(422, 'The two categories both have a budget target for 2026-10.', 'rate-unavailable', missing),
        );
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Dining' }),
        ]);
        const dialog = await openMerge('Groceries');
        await userEvent.click(within(dialog).getByRole('combobox'));
        await userEvent.click(await screen.findByRole('option', { name: 'Dining' }));
        await userEvent.click(within(dialog).getByRole('button', { name: 'Merge' }));

        const alert = await within(dialog).findByRole('alert');
        expect(alert).toHaveTextContent(
            'Nothing was merged. Both categories have a budget target for October 2026, one in CAD and one in USD, '
            + 'and there is no CAD to USD rate for Oct 31, 2026 to add them with. Add that rate, then merge again.');
        expect(within(alert).getByRole('link', { name: 'Exchange rates →' }))
            .toHaveAttribute('href', expect.stringContaining('/settings?tab=rates'));
        expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('keeps the dialog open and says why when the server refuses', async () => {
        vi.spyOn(apiModule, 'mergeCategory').mockRejectedValue(
            new apiModule.ApiError(422, 'A category cannot be merged into its own descendant.', 'category-cycle'),
        );
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Dining' }),
        ]);
        const dialog = await openMerge('Groceries');
        await userEvent.click(within(dialog).getByRole('combobox'));
        await userEvent.click(await screen.findByRole('option', { name: 'Dining' }));
        await userEvent.click(within(dialog).getByRole('button', { name: 'Merge' }));

        expect(await within(dialog).findByRole('alert')).toHaveTextContent('into its own descendant');
        expect(screen.getByRole('dialog')).toBeInTheDocument();
    });
});

describe('deactivating and reactivating a category', () => {
    it('asks first, then deactivates, and says what it does', async () => {
        const setActive = vi.spyOn(apiModule, 'setAccountActive').mockResolvedValue(undefined);
        renderPanel([node({ id: 'c1', name: 'Groceries' }), node({ id: 'c2', name: 'Dining' })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Deactivate' }));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText(/can no longer be chosen for a new transaction/)).toBeInTheDocument();
        // Asked, not done: nothing has been sent.
        expect(setActive).not.toHaveBeenCalled();
        await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));

        await waitFor(() => expect(setActive).toHaveBeenCalledWith(LEDGER_ID, 'c1', false));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    });

    it('does nothing when the question is cancelled', async () => {
        const setActive = vi.spyOn(apiModule, 'setAccountActive').mockResolvedValue(undefined);
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Deactivate' }));
        await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }));

        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
        expect(setActive).not.toHaveBeenCalled();
    });

    it('keeps the question open and says why when the server refuses', async () => {
        vi.spyOn(apiModule, 'setAccountActive').mockRejectedValue(
            new apiModule.ApiError(422, 'System accounts cannot be deactivated.', 'account-is-system'),
        );
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Deactivate' }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));

        expect(await within(dialog).findByText('System accounts cannot be deactivated.')).toBeInTheDocument();
        expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('brings an inactive category back from its menu without asking', async () => {
        // A merge deactivates its source, and Reactivate is how it comes back: as an empty category,
        // which is what the merge dialog says.
        const setActive = vi.spyOn(apiModule, 'setAccountActive').mockResolvedValue(undefined);
        const groceries = node({ id: 'c1', name: 'Groceries' });
        const old = node({ id: 'c2', name: 'Old', isActive: false });
        renderPanel([groceries], { withInactive: [groceries, old] });

        await userEvent.click(await screen.findByLabelText('Show inactive'));
        expect(await screen.findByText('Old')).toBeInTheDocument();
        // The row is marked, in words as well as by being struck through.
        expect(within(screen.getByText('Old').closest('li')!).getByText('Inactive')).toBeInTheDocument();

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Old' }));
        // An inactive category is not worth giving a sub-category.
        expect(screen.queryByRole('menuitem', { name: 'Add sub-category' })).not.toBeInTheDocument();
        expect(screen.queryByRole('menuitem', { name: 'Deactivate' })).not.toBeInTheDocument();
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Reactivate' }));

        await waitFor(() => expect(setActive).toHaveBeenCalledWith(LEDGER_ID, 'c2', true));
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
});

describe('the kind headings', () => {
    it('are the same heading part as the account groups: the label, then the count in parentheses', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Dining' }),
            node({ id: 'c3', name: 'Salary', categoryKind: 'income' }),
        ]);

        expect(await screen.findByRole('heading', { level: 3, name: /^Expense\s*\(2\)$/ })).toBeInTheDocument();
        expect(screen.getByRole('heading', { level: 3, name: /^Income\s*\(1\)$/ })).toBeInTheDocument();
    });
});

describe('where focus goes around a dialog opened from a row', () => {
    // A real dialog, not a stand-in: Modal gives focus back to whatever had it when it opened, and
    // a field that grabs focus itself on mount is focused before Modal looks, so closing would
    // leave a keyboard reader on the page body.
    it('returns to the row’s button when Rename is closed, and starts in the name field', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        const kebab = await screen.findByRole('button', { name: 'Actions for Groceries' });

        await userEvent.click(kebab);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename…' }));
        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByLabelText('Name')).toHaveFocus();

        await userEvent.keyboard('{Escape}');

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        expect(kebab).toHaveFocus();
    });

    it('returns to the row’s button when Add sub-category is cancelled', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        const kebab = await screen.findByRole('button', { name: 'Actions for Groceries' });

        await userEvent.click(kebab);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Add sub-category' }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        expect(kebab).toHaveFocus();
    });

    it('returns to the row’s button when the merge Delete handed over to is closed', async () => {
        // "Merge instead…" closes the delete confirm and opens the merge dialog in one update.
        renderPanel([
            node({ id: 'c1', name: 'Groceries', transactionCount: 4 }),
            node({ id: 'c2', name: 'Dining' }),
        ]);
        const kebab = await screen.findByRole('button', { name: 'Actions for Groceries' });

        await userEvent.click(kebab);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
        const confirm = await screen.findByRole('dialog');
        await userEvent.click(within(confirm).getByRole('button', { name: 'Merge instead…' }));
        const merge = await screen.findByRole('dialog', { name: /^Merge/ });
        expect(merge.contains(document.activeElement)).toBe(true);

        // The destination picker opens its list on focus: the first Escape closes the list, the
        // second the dialog.
        await userEvent.keyboard('{Escape}');
        expect(screen.getByRole('dialog', { name: /^Merge/ })).toBeInTheDocument();
        await userEvent.keyboard('{Escape}');

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        expect(kebab).toHaveFocus();
    });
});

// A category has no currency of its own (ADR-0109 D9): the server stamps the reporting
// currency when none is sent and refuses any other with category-currency-not-reporting.
// The dialogs send none, so that refusal cannot reach a reader.
describe('a category write sends no currency', () => {
    it('New category posts no currency code', async () => {
        const create = vi.spyOn(apiModule, 'createAccount').mockImplementation(() => new Promise(() => {}));
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'New category' }));
        const dialog = await screen.findByRole('dialog');
        await user.type(within(dialog).getByLabelText('Name'), 'Produce');
        await user.click(within(dialog).getByRole('button', { name: 'Create' }));

        await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
        expect(create.mock.calls[0]![1]).not.toHaveProperty('currencyCode');
    });

    it('Rename patches the name only', async () => {
        const update = vi.spyOn(apiModule, 'updateAccount').mockImplementation(() => new Promise(() => {}));
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await user.click(await screen.findByRole('menuitem', { name: /^Rename/ }));
        const dialog = await screen.findByRole('dialog');
        const name = within(dialog).getByLabelText('Name');
        await user.clear(name);
        await user.type(name, 'Food');
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
        expect(update.mock.calls[0]![2]).toEqual({ name: 'Food' });
    });
});

// ADR-0023 §L: a dialog's fields are pending while its save is in flight. The
// save's success closes the dialog, so a change made meanwhile would be dropped,
// and the save sends what the fields held at the press.
describe('a category dialog while it saves', () => {
    it('Add sub-category refuses typing in the name', async () => {
        vi.spyOn(apiModule, 'createAccount').mockImplementation(() => new Promise(() => {}));
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Add sub-category' }));
        const dialog = await screen.findByRole('dialog');
        const name = within(dialog).getByLabelText('Name');
        await user.type(name, 'Produce');
        await user.click(within(dialog).getByRole('button', { name: 'Create' }));
        await within(dialog).findByRole('button', { name: 'Creating…' });

        await user.type(name, ' more');
        expect(name).toHaveValue('Produce');
        expect(name).toHaveAttribute('aria-disabled', 'true');
    });

    it('New category refuses a change of kind or of Top level', async () => {
        vi.spyOn(apiModule, 'createAccount').mockImplementation(() => new Promise(() => {}));
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'New category' }));
        const dialog = await screen.findByRole('dialog');
        const kind = within(dialog).getByLabelText('Kind');
        const topLevel = within(dialog).getByRole('checkbox', { name: /top level/i }) as HTMLInputElement;
        const kindBefore = (kind as HTMLSelectElement).value;
        await user.type(within(dialog).getByLabelText('Name'), 'Produce');
        await user.click(within(dialog).getByRole('button', { name: 'Create' }));
        await within(dialog).findByRole('button', { name: 'Creating…' });

        await user.selectOptions(kind, kindBefore === 'income' ? 'expense' : 'income');
        await user.click(topLevel);
        expect(kind).toHaveValue(kindBefore);
        expect(topLevel.checked).toBe(true);
        expect(kind).toHaveAttribute('aria-disabled', 'true');
        expect(topLevel).toHaveAttribute('aria-disabled', 'true');
    });

    it('Move refuses a change of Top level', async () => {
        vi.spyOn(apiModule, 'reparentCategory').mockImplementation(() => new Promise(() => {}));
        renderPanel([
            node({ id: 'c1', name: 'Groceries', childCount: 1 }),
            node({ id: 'c2', name: 'Produce', parentId: 'c1' }),
        ]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for Groceries/Produce' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Move…' }));
        const dialog = await screen.findByRole('dialog');
        const topLevel = within(dialog).getByRole('checkbox', { name: /top level/i }) as HTMLInputElement;
        await user.click(topLevel);
        await user.click(within(dialog).getByRole('button', { name: 'Move' }));
        await within(dialog).findByRole('button', { name: 'Moving…' });

        await user.click(topLevel);
        expect(topLevel.checked).toBe(true);
        expect(topLevel).toHaveAttribute('aria-disabled', 'true');
    });

    // The pickers below are field components: while the save is in flight a
    // press opens no list, typing changes nothing and Enter commits no pick.
    // Each attempt names a category the picker offers, so without the pending
    // state it would land.
    it('New category refuses a change of parent', async () => {
        vi.spyOn(apiModule, 'createAccount').mockImplementation(() => new Promise(() => {}));
        renderPanel([node({ id: 'c1', name: 'Groceries' }), node({ id: 'c2', name: 'Dining' })]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'New category' }));
        const dialog = await screen.findByRole('dialog');
        await user.type(within(dialog).getByLabelText('Name'), 'Produce');
        await user.click(within(dialog).getByRole('checkbox', { name: /top level/i }));
        const parent = within(dialog).getByRole('combobox', { name: 'Account or category' });
        await user.click(parent);
        await user.click(await screen.findByRole('option', { name: 'Groceries' }));
        await user.click(within(dialog).getByRole('button', { name: 'Create' }));
        await within(dialog).findByRole('button', { name: 'Creating…' });

        await user.click(parent);
        await user.keyboard('Dining{ArrowDown}{Enter}');
        expect(parent).toHaveValue('Groceries');
        expect(parent).toHaveAttribute('aria-expanded', 'false');
        expect(parent).toHaveAttribute('aria-disabled', 'true');
    });

    it('Move refuses a change of parent', async () => {
        vi.spyOn(apiModule, 'reparentCategory').mockImplementation(() => new Promise(() => {}));
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Dining' }),
            node({ id: 'c3', name: 'Travel' }),
        ]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for Dining' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Move…' }));
        const dialog = await screen.findByRole('dialog');
        const parent = within(dialog).getByRole('combobox', { name: 'Account or category' });
        await user.click(parent);
        await user.click(await screen.findByRole('option', { name: 'Groceries' }));
        await user.click(within(dialog).getByRole('button', { name: 'Move' }));
        await within(dialog).findByRole('button', { name: 'Moving…' });

        await user.click(parent);
        await user.keyboard('Travel{ArrowDown}{Enter}');
        expect(parent).toHaveValue('Groceries');
        expect(parent).toHaveAttribute('aria-expanded', 'false');
        expect(parent).toHaveAttribute('aria-disabled', 'true');
    });

    it('Merge refuses a change of the category it merges into', async () => {
        vi.spyOn(apiModule, 'mergeCategory').mockImplementation(() => new Promise(() => {}));
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Dining' }),
            node({ id: 'c3', name: 'Travel' }),
        ]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Merge…' }));
        const dialog = await screen.findByRole('dialog');
        const target = within(dialog).getByRole('combobox', { name: 'Account or category' });
        await user.click(target);
        await user.click(await screen.findByRole('option', { name: 'Dining' }));
        await user.click(within(dialog).getByRole('button', { name: 'Merge' }));
        await within(dialog).findByRole('button', { name: 'Merging…' });

        await user.click(target);
        await user.keyboard('Travel{ArrowDown}{Enter}');
        expect(target).toHaveValue('Dining');
        expect(target).toHaveAttribute('aria-expanded', 'false');
        expect(target).toHaveAttribute('aria-disabled', 'true');
    });

    it('Rename refuses typing in the name', async () => {
        vi.spyOn(apiModule, 'updateAccount').mockImplementation(() => new Promise(() => {}));
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Rename…' }));
        const dialog = await screen.findByRole('dialog');
        const name = within(dialog).getByLabelText('Name');
        await user.type(name, ' Two');
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));
        await within(dialog).findByRole('button', { name: 'Saving…' });

        await user.type(name, ' more');
        expect(name).toHaveValue('Groceries Two');
        expect(name).toHaveAttribute('aria-disabled', 'true');
    });
});

describe('what the list does while it works', () => {
    it('says so when a category cannot be brought back, and says it where an assistive reader hears it', async () => {
        // Reactivating asks nothing, so there is no dialog to say it failed in.
        vi.spyOn(apiModule, 'setAccountActive').mockRejectedValue(
            new apiModule.ApiError(422, 'That category is not in this ledger.', 'account-not-in-ledger'),
        );
        const groceries = node({ id: 'c1', name: 'Groceries' });
        const old = node({ id: 'c2', name: 'Old', isActive: false });
        renderPanel([groceries], { withInactive: [groceries, old] });

        await userEvent.click(await screen.findByLabelText('Show inactive'));
        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Old' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Reactivate' }));

        expect(await screen.findByRole('alert')).toHaveTextContent('That category is not in this ledger.');
    });

    it('keeps the rows and the toolbar in place while "Show inactive" fetches the longer list', async () => {
        // The key changes when the box is ticked. Without the rows kept, the list blinks to a
        // skeleton and the checkbox the reader is standing on is disabled under them.
        const groceries = node({ id: 'c1', name: 'Groceries' });
        const old = node({ id: 'c2', name: 'Old', isActive: false });
        const { queryClient } = renderPanel([groceries]);
        queryClient.removeQueries({ queryKey: ['categories', LEDGER_ID, true] });
        let release: (list: CategoryList) => void = () => {};
        const fetchCategories = vi.spyOn(apiModule, 'fetchCategories').mockImplementation(
            () => new Promise((resolve) => { release = resolve; }));
        await screen.findByText('Groceries');

        const box = screen.getByLabelText('Show inactive');
        await userEvent.click(box);
        await waitFor(() => expect(fetchCategories).toHaveBeenCalled());

        expect(screen.getByText('Groceries')).toBeInTheDocument();
        expect(screen.queryByLabelText('Loading categories')).not.toBeInTheDocument();
        expect(box).toBeEnabled();
        expect(screen.getByRole('searchbox', { name: 'Search categories' })).toBeEnabled();

        release(listOf([groceries, old]));
        expect(await screen.findByText('Old')).toBeInTheDocument();
    });

    it('announces a refusal inside the delete confirm', async () => {
        vi.spyOn(apiModule, 'deleteCategory').mockRejectedValue(
            new apiModule.ApiError(422, 'That category cannot be deleted.', 'category-in-use'),
        );
        renderPanel([node({ id: 'c1', name: 'Groceries' }), node({ id: 'c2', name: 'Dining' })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

        expect(await within(dialog).findByRole('alert')).toHaveTextContent('That category cannot be deleted.');
    });

    it('announces a refusal inside the deactivate confirm', async () => {
        vi.spyOn(apiModule, 'setAccountActive').mockRejectedValue(
            new apiModule.ApiError(422, 'System accounts cannot be deactivated.', 'account-is-system'),
        );
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Deactivate' }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));

        expect(await within(dialog).findByRole('alert')).toHaveTextContent('System accounts cannot be deactivated.');
    });
});

describe('New category', () => {
    it('opens the create dialog from the toolbar, defaulting to expense', async () => {
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        await userEvent.click(await screen.findByRole('button', { name: 'New category' }));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByRole('heading', { name: 'New category' })).toBeInTheDocument();
        expect(within(dialog).getByLabelText('Kind')).toHaveValue('expense');
    });

    it('offers the same from an empty ledger, where there is nothing else on the page', async () => {
        renderPanel([]);
        expect(await screen.findByText('No categories yet')).toBeInTheDocument();
        expect(screen.getAllByRole('button', { name: /New category/ })).toHaveLength(2);
    });

    it('offers a reader who cannot edit nothing to make one with', async () => {
        renderPanel([], { role: 'viewer' });
        expect(await screen.findByText('No categories yet')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /New category/ })).not.toBeInTheDocument();
    });
});

describe('the adjustment kind (mig 224)', () => {
    it('lists an adjustment category instead of silently dropping it', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'House Revaluation', categoryKind: 'adjustment' }),
        ]);

        // A panel with exactly two KindSections, each filtered on
        // `categoryKind === kind`, would fetch this row from the API and then
        // drop it on the floor. Anchored on the expense row, which is never in
        // doubt, so this cannot pass because nothing rendered.
        expect(await screen.findByText('Groceries')).toBeInTheDocument();
        expect(screen.getByText('House Revaluation')).toBeInTheDocument();
    });

    it('leaves a kind out when the ledger has none of it', async () => {
        // There is no per-kind New button to reach an empty section with any more: New category is
        // the toolbar's, and its dialog asks for the kind. So an empty section is only noise.
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        await screen.findByText('Groceries');
        expect(screen.queryByRole('heading', { name: /Adjustment/ })).not.toBeInTheDocument();
        expect(screen.queryByRole('heading', { name: /Income/ })).not.toBeInTheDocument();
        expect(screen.queryByText(/No adjustment categories yet/)).not.toBeInTheDocument();
        expect(screen.getByRole('heading', { name: /Expense/ })).toBeInTheDocument();
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

describe('a category\'s figures are in the ledger\'s currency (ADR-0109 D9)', () => {
    // A category has no currency of its own: its lines carry the paying account's. The code
    // stored on a category's account row is therefore never what its figure wears.
    const category = (id: string, currencyCode: string): AccountSummary => ({
        id, ledgerId: LEDGER_ID, parentId: null, name: id, accountType: 'category', categoryKind: 'expense',
        currencyCode, isActive: true, isSystem: false, feedConnectionId: null, needsReviewCount: 0,
        holdingsAccountId: null, isTradeCommission: false,
    });
    const section = (label: string) =>
        screen.getByRole('heading', { name: new RegExp(label, 'i') }).closest('section')!;
    const totalOf = (label: string) => section(label).querySelector('h3')!.parentElement!;
    const rowOf = (name: string) => screen.getByRole('link', { name }).closest('li')!;
    const twoCategories = () =>
        [node({ id: 'c1', name: 'Category A', total: 10 }), node({ id: 'c2', name: 'Category B', total: 5 })];

    it('labels each row and a kind\'s total in the ledger\'s currency, not a category\'s stored one', async () => {
        renderPanel(twoCategories(), {
            accounts: [category('c1', 'GBP'), category('c2', 'JPY')],
            currencyCode: 'EUR',
        });

        await screen.findByText('Category A');
        expect(rowOf('Category A')).toHaveTextContent(formatCurrency(10, 'EUR'));
        expect(rowOf('Category B')).toHaveTextContent(formatCurrency(5, 'EUR'));
        expect(totalOf('Expense')).toHaveTextContent(formatCurrency(15, 'EUR'));
        expect(section('Expense').textContent).not.toMatch(/[£¥$]/);
    });

    it('says "no rate" for a kind\'s total when a category\'s total needs a missing rate, muted, with the rate on hover', async () => {
        const missing = { fromCurrency: 'GBP', toCurrency: 'EUR', date: '2026-01-30', reason: 'rate_unavailable' as const };
        renderPanel(
            [node({ id: 'c1', name: 'Category A', total: 10 }), node({ id: 'c2', name: 'Category B', total: null, rateUnavailable: [missing] })],
            { accounts: [category('c1', 'EUR'), category('c2', 'EUR')], currencyCode: 'EUR' },
        );

        await screen.findByText('Category A');
        const total = within(totalOf('Expense')).getByText('no rate');
        expect(total).toHaveAttribute('title', 'No GBP to EUR rate for Jan 30, 2026.');
        // The marker is muted, not drawn as a figure.
        expect(total).toHaveClass('text-text-subtle');
        expect(totalOf('Expense')).not.toHaveTextContent(formatCurrency(10, 'EUR'));
        // A category with its rates keeps its figure; the other says why it has none.
        expect(rowOf('Category A')).toHaveTextContent(formatCurrency(10, 'EUR'));
        expect(within(rowOf('Category B')).getByText('no rate')).toHaveAttribute('title', 'No GBP to EUR rate for Jan 30, 2026.');
    });

    it('says above the list that totals are converted, when an account is in another currency', async () => {
        const { queryClient } = renderPanel(twoCategories());
        queryClient.setQueryData(['ledgers'], [{ id: LEDGER_ID, name: 'Ledger', role: 'owner', currencyCode: 'USD' }]);
        queryClient.setQueryData(['accounts', LEDGER_ID, { includeInactive: true }], [
            { ...category('c1', 'USD'), id: 'acct-a', name: 'Account A', accountType: 'bank', categoryKind: null },
            { ...category('c2', 'USD'), id: 'acct-b', name: 'Account B', accountType: 'bank', categoryKind: null, currencyCode: 'CAD' },
        ]);

        expect(await screen.findByText(/^Totals are in USD, with CAD accounts converted at the rate for each figure's date \(the latest rate from today on\)\./)).toBeInTheDocument();
    });

    it('carries no note when every account is in the reporting currency', async () => {
        const { queryClient } = renderPanel(twoCategories());
        queryClient.setQueryData(['ledgers'], [{ id: LEDGER_ID, name: 'Ledger', role: 'owner', currencyCode: 'USD' }]);
        queryClient.setQueryData(['accounts', LEDGER_ID, { includeInactive: true }], [
            { ...category('c1', 'USD'), id: 'acct-a', name: 'Account A', accountType: 'bank', categoryKind: null },
            category('c2', 'GBP'),
        ]);

        await screen.findByText('Category A');
        expect(screen.queryByText(/^Totals are in/)).toBeNull();
    });

    it('gives a kind\'s total in one currency its own title, not the mixed one', async () => {
        renderPanel(twoCategories(), {
            accounts: [category('c1', 'EUR'), category('c2', 'GBP')],
            currencyCode: 'EUR',
        });

        await screen.findByText('Category A');
        const total = within(totalOf('Expense')).getByText(formatCurrency(15, 'EUR'));
        expect(total).toHaveAttribute('title', 'Total expense across all expense categories');
        expect(total).not.toHaveClass('text-text-subtle');
        expect(totalOf('Expense')).not.toHaveTextContent('mixed');
    });

    it('labels an inactive category, drawn with "Show inactive", in the ledger\'s currency', async () => {
        // The ledger's account read holds active accounts only, and nothing else is asked.
        const active = node({ id: 'c1', name: 'Category A', total: 10 });
        const old = node({ id: 'c2', name: 'Category B', total: 5, isActive: false });
        renderPanel([active], {
            withInactive: [active, old],
            accounts: [category('c1', 'GBP')],
            currencyCode: 'EUR',
        });

        await userEvent.click(await screen.findByLabelText('Show inactive'));
        await screen.findByText('Category B');
        expect(rowOf('Category B')).toHaveTextContent(formatCurrency(5, 'EUR'));
        expect(totalOf('Expense')).toHaveTextContent(formatCurrency(15, 'EUR'));
        expect(section('Expense').textContent).not.toMatch(/[£$]/);
    });

    it('prints a category\'s figure though no account read holds the category', async () => {
        renderPanel(twoCategories(), { accounts: [category('c1', 'GBP')], currencyCode: 'EUR' });

        await screen.findByText('Category B');
        expect(rowOf('Category B')).toHaveTextContent(formatCurrency(5, 'EUR'));
        expect(totalOf('Expense')).toHaveTextContent(formatCurrency(15, 'EUR'));
    });
});

describe('sections collapse, because a real taxonomy is long', () => {
    // The Demo's expense categories alone run to about eighty, so anything below them is
    // ~2500px down. Adding a third section made that obvious; collapsing is
    // the general fix, not an Adjustment-specific one.
    //
    // The section headings are matched by the COUNT suffix. Their accessible
    // name is "Expense (1)". A FUNCTION matcher, not a regex built by template
    // literal: `\(` and `\d` inside a template literal are resolved by JS before
    // RegExp ever sees them, so the pattern silently becomes `^Expense (d+)$` and
    // matches nothing — a 5s timeout, not a bad-pattern error.
    const section = (label: string) => (name: string) =>
        name.startsWith(label) && /\(\d+\)\s*$/.test(name);

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
        // must survive it. A collapse that took the whole section away would
        // trade one scroll for a worse one.
        expect(screen.getByText('Paycheck')).toBeVisible();
        expect(expense.closest('section')!.firstElementChild!.textContent).toContain('$0.00');
    });

    it('starts every section collapsed on a ledger with no stored preference', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Paycheck', categoryKind: 'income', total: -9200 }),
        ], { pristine: true });

        for (const label of ['Income', 'Expense']) {
            expect(await screen.findByRole('button', { name: section(label) }))
                .toHaveAttribute('aria-expanded', 'false');
        }
        expect(screen.getByText('Groceries')).not.toBeVisible();

        // The totals survive the collapse — that is what makes the folded
        // screen a summary rather than dead rows. Scoped to the section
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
        const both = [
            node({ id: 'c1', name: 'Groceries' }),
            node({ id: 'c2', name: 'Paycheck', categoryKind: 'income', total: -9200 }),
        ];
        const first = renderPanel(both, { pristine: true });
        fireEvent.click(await screen.findByRole('button', { name: section('Expense') }));
        expect(screen.getByText('Groceries')).toBeVisible();
        first.unmount();

        renderPanel(both, { pristine: true });

        // Income is the control: untouched, so it must still read the default.
        expect(await screen.findByRole('button', { name: section('Expense') }))
            .toHaveAttribute('aria-expanded', 'true');
        expect(screen.getByRole('button', { name: section('Income') }))
            .toHaveAttribute('aria-expanded', 'false');
    });
});

describe('searching categories', () => {
    const tree = [
        node({ id: 'a', name: 'Auto', childCount: 2, total: 500 }),
        node({ id: 'f', name: 'Fuel', parentId: 'a', total: 300 }),
        node({ id: 'p', name: 'Parking', parentId: 'a', total: 200 }),
        node({ id: 'g', name: 'Groceries', total: 100 }),
        node({ id: 'i', name: 'Salary', categoryKind: 'income', total: -900 }),
    ];

    it('keeps a match together with the path that leads to it', async () => {
        renderPanel(tree);
        await screen.findByText('Fuel');

        await userEvent.type(screen.getByRole('searchbox', { name: 'Search categories' }), 'fuel');

        // Fuel, and Auto above it, which is what makes Fuel mean something; not Parking, Groceries or Salary.
        expect(screen.getByText('Fuel')).toBeInTheDocument();
        expect(screen.getByText('Auto')).toBeInTheDocument();
        expect(screen.queryByText('Parking')).not.toBeInTheDocument();
        expect(screen.queryByText('Groceries')).not.toBeInTheDocument();
        // A kind with nothing in the result is gone, not left empty.
        expect(screen.queryByRole('heading', { name: /Income/ })).not.toBeInTheDocument();
    });

    it('opens the collapsed sections it found something in, and leaves the totals off', async () => {
        renderPanel(tree, { pristine: true });
        await screen.findByText('Groceries');
        expect(screen.getByText('Groceries')).not.toBeVisible();

        await userEvent.type(screen.getByRole('searchbox', { name: 'Search categories' }), 'grocer');

        expect(screen.getByText('Groceries')).toBeVisible();
        // A total of part of the tree would read as the whole.
        const heading = screen.getByRole('heading', { name: /Expense/ });
        expect(heading.closest('section')!.firstElementChild!.textContent).not.toMatch(/\$/);
    });

    it('says so when nothing matches, and clears', async () => {
        renderPanel(tree);
        await screen.findByText('Fuel');

        await userEvent.type(screen.getByRole('searchbox', { name: 'Search categories' }), 'zzz');
        expect(await screen.findByText('Nothing matches “zzz”')).toBeInTheDocument();
        expect(screen.queryByText('Fuel')).not.toBeInTheDocument();

        await userEvent.click(screen.getByRole('button', { name: 'Clear search' }));
        expect(await screen.findByText('Fuel')).toBeInTheDocument();
    });
});

describe('deleting a category', () => {
    it('says the budget targets go with an empty category', async () => {
        // The server counts transactions and sub-categories before it deletes, and not the budget
        // targets, which cascade away with the category; the confirmation is where that is said.
        vi.spyOn(apiModule, 'deleteCategory').mockResolvedValue(undefined);
        renderPanel([node({ id: 'c1', name: 'Groceries' })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText(/any budget targets set on it go with it/)).toBeInTheDocument();
    });

    it('explains and offers Merge instead for a category in use', async () => {
        renderPanel([
            node({ id: 'c1', name: 'Groceries', transactionCount: 4 }),
            node({ id: 'c2', name: 'Dining' }),
        ]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for Groceries' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));

        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByText('Can’t delete “Groceries”')).toBeInTheDocument();
        expect(within(dialog).getByRole('button', { name: 'Merge instead…' })).toBeInTheDocument();
    });
});
