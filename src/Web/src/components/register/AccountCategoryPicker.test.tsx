import { describe, it, expect, beforeEach, onTestFinished, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { AccountCategoryPicker } from './AccountCategoryPicker';
import { Modal } from '@/components/ui/Modal';
import type { AccountSummary } from '@/lib/types';

// jsdom doesn't implement scrollIntoView; the highlight-scroll effect calls it.
beforeEach(() => {
    Element.prototype.scrollIntoView = vi.fn();
});

function cat(id: string, name: string, parentId: string | null): AccountSummary {
    return {
        id, name, parentId,
        ledgerId: 'led', accountType: 'category', categoryKind: 'expense',
        currencyCode: 'USD', isActive: true, isSystem: false,
    } as AccountSummary;
}

//  Auto            Bills
//    Gas             Cable Television
//                    Electricity
const CATS: AccountSummary[] = [
    cat('bills', 'Bills', null),
    cat('elec', 'Electricity', 'bills'),
    cat('cable', 'Cable Television', 'bills'),
    cat('auto', 'Auto', null),
    cat('gas', 'Gas', 'auto'),
];
const isCategory = (a: AccountSummary) => a.accountType === 'category';

function renderPicker(onChangeId = vi.fn()) {
    render(
        <AccountCategoryPicker
            accounts={CATS}
            isEligible={isCategory}
            valueId={null}
            onChangeId={onChangeId}
            label="Category"
            ariaLabel="Category"
        />,
    );
    return { onChangeId };
}

describe('AccountCategoryPicker (tree-aware)', () => {
    it('renders categories as a root-first, indented tree (parents before their children)', async () => {
        renderPicker();
        await userEvent.click(screen.getByRole('combobox', { name: 'Category' }));

        const options = screen.getAllByRole('option').map((o) => o.textContent);
        // Roots alpha (Auto before Bills), children nested under each parent.
        expect(options).toEqual(['Auto', 'Gas', 'Bills', 'Cable Television', 'Electricity']);
    });

    it('path-typing narrows to the target leaf and shows its ancestor for context', async () => {
        renderPicker();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await userEvent.click(input);
        // 'Bills/Elec' scopes to Bills' subtree; 'elec' matches Electricity only
        // (unlike 'el', which would also substring-hit Cable Tel-el-evision).
        await userEvent.type(input, 'Bills/Elec');

        const options = screen.getAllByRole('option').map((o) => o.textContent);
        expect(options).toEqual(['Bills', 'Electricity']); // Auto branch + Cable pruned
    });

    it('commits by exact full path on Enter (the copy/paste round-trip)', async () => {
        const { onChangeId } = renderPicker();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await userEvent.click(input);
        await userEvent.type(input, 'Bills/Electricity{Enter}');

        expect(onChangeId).toHaveBeenCalledWith('elec');
    });
});

describe('fitting a tight container', () => {
    // The panel is fixed to the viewport and closes on scroll, so it does not
    // have to fit inside a scrolling ancestor. `compact` is a shorter
    // ceiling for containers where a tall list overwhelms the row.
    it('shortens the results list when compact', async () => {
        const user = userEvent.setup();
        const { container } = render(
            <AccountCategoryPicker
                accounts={CATS}
                isEligible={isCategory}
                valueId={null}
                onChangeId={vi.fn()}
                ariaLabel="Category"
                compact
            />,
        );
        await user.click(screen.getByRole('combobox', { name: 'Category' }));
        // A measured value now, not a class: the panel is fixed to the viewport
        // and the list is capped by the room actually below the control, so a
        // token could not express it. `compact` lowers the ceiling it is capped
        // AGAINST, which is what this still pins.
        const list = container.querySelector('ul');
        expect(list).not.toBeNull();
        const compactHeight = Number.parseInt(list!.style.maxHeight, 10);
        expect(compactHeight).toBeGreaterThan(0);
        expect(compactHeight).toBeLessThanOrEqual(160);
    });

    it('keeps the full list height by default', async () => {
        const user = userEvent.setup();
        const { container } = render(
            <AccountCategoryPicker
                accounts={CATS}
                isEligible={isCategory}
                valueId={null}
                onChangeId={vi.fn()}
                ariaLabel="Category"
            />,
        );
        await user.click(screen.getByRole('combobox', { name: 'Category' }));
        // jsdom reports a 768px window and a zero-sized control, so there is
        // room for the full cap here; the point is that the default is NOT
        // clamped to compact's 160.
        const list = container.querySelector('ul');
        expect(list).not.toBeNull();
        expect(Number.parseInt(list!.style.maxHeight, 10)).toBeGreaterThan(160);
    });
});

describe('closing on scroll', () => {
    // The panel is fixed to the viewport, so it cannot follow a scroller it has
    // escaped: it closes instead. The distinction that matters is WHOSE scroll.
    async function openInsideAScroller() {
        const user = userEvent.setup();
        const view = render(
            <div data-testid="scroller">
                <AccountCategoryPicker
                    accounts={CATS}
                    isEligible={isCategory}
                    valueId={null}
                    onChangeId={vi.fn()}
                    ariaLabel="Category"
                />
            </div>,
        );
        await user.click(screen.getByRole('combobox', { name: 'Category' }));
        // The listener goes on a frame late, so opening does not close itself.
        await act(async () => { await new Promise(requestAnimationFrame); });
        expect(screen.queryByRole('listbox')).not.toBeNull();
        return view;
    }

    it('stays open while the user scrolls the list', async () => {
        await openInsideAScroller();
        const list = screen.getByRole('listbox');

        // Dragging the list's own scrollbar. This reaches the window capture
        // listener exactly like an ancestor's scroll does, and closing on it
        // made the panel disappear the moment the scrollbar was touched.
        await act(async () => { list.dispatchEvent(new Event('scroll')); });

        expect(screen.queryByRole('listbox')).not.toBeNull();
    });

    it('closes when the container underneath scrolls', async () => {
        await openInsideAScroller();

        await act(async () => {
            screen.getByTestId('scroller').dispatchEvent(new Event('scroll'));
        });

        expect(screen.queryByRole('listbox')).toBeNull();
    });
});

// A money account beside the categories, so the panel shows its filter buttons.
const WITH_ACCOUNT: AccountSummary[] = [
    ...CATS,
    { ...cat('savings', 'Savings', null), accountType: 'bank', categoryKind: null },
];
const isAnything = () => true;

describe('AccountCategoryPicker when focus leaves it', () => {
    // The list opens on focus. Left open after focus moves on, it is still on
    // screen, but the Escape that should close it lands on the other field and
    // reaches the editor, which cancels the edit. Focus leaving closes it.
    function renderBetweenFields(onChangeId = vi.fn(), accounts: AccountSummary[] = CATS) {
        const parentEscapes: { defaultPrevented: boolean }[] = [];
        // Every Escape as the picker left it: the native event, taken by a
        // capture listener before anything handles it and read once it is
        // done; and the ones that went on past the React tree to the window.
        const escapes: KeyboardEvent[] = [];
        const windowEscapes: KeyboardEvent[] = [];
        const capture = (e: KeyboardEvent) => { if (e.key === 'Escape') escapes.push(e); };
        const bubble = (e: KeyboardEvent) => { if (e.key === 'Escape') windowEscapes.push(e); };
        window.addEventListener('keydown', capture, true);
        window.addEventListener('keydown', bubble);
        onTestFinished(() => {
            window.removeEventListener('keydown', capture, true);
            window.removeEventListener('keydown', bubble);
        });
        render(
            <div
                onKeyDown={(e) => {
                    if (e.key === 'Escape') parentEscapes.push({ defaultPrevented: e.defaultPrevented });
                }}
            >
                <input aria-label="Before" />
                <AccountCategoryPicker
                    accounts={accounts}
                    isEligible={isAnything}
                    valueId={null}
                    onChangeId={onChangeId}
                    ariaLabel="Category"
                />
                <input aria-label="After" />
            </div>,
        );
        const marks = () => escapes.map((e) => e.defaultPrevented);
        return { parentEscapes, windowEscapes, marks, onChangeId };
    }

    it('closes the list when Tab leaves it, so the next Escape is the editor\'s', async () => {
        const user = userEvent.setup();
        const { parentEscapes } = renderBetweenFields();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.tab();
        expect(screen.getByLabelText('After')).toHaveFocus();
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('listbox')).toBeNull();

        await user.keyboard('{Escape}');
        expect(parentEscapes).toEqual([{ defaultPrevented: false }]);
    });

    it('closes the list when Shift+Tab leaves it', async () => {
        const user = userEvent.setup();
        renderBetweenFields();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.tab({ shift: true });
        expect(screen.getByLabelText('Before')).toHaveFocus();
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('listbox')).toBeNull();
    });

    it('commits a click on an option', async () => {
        const user = userEvent.setup();
        const { onChangeId } = renderBetweenFields();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);

        await user.click(screen.getByRole('option', { name: 'Gas' }));

        expect(onChangeId).toHaveBeenCalledWith('gas');
        expect(input).toHaveAttribute('aria-expanded', 'false');
    });

    it('keeps the list open while Tab moves through its filter buttons', async () => {
        // With accounts and categories both eligible the panel carries
        // All · Accounts · Categories, and Tab reaches them before the next
        // field. They are inside the control, so the list stays.
        const user = userEvent.setup();
        renderBetweenFields(vi.fn(), WITH_ACCOUNT);
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);

        await user.tab();
        expect(screen.getByRole('button', { name: /^All/ })).toHaveFocus();
        expect(input).toHaveAttribute('aria-expanded', 'true');

        await user.tab();
        await user.tab();
        expect(screen.getByRole('button', { name: /^Categories/ })).toHaveFocus();
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.tab();
        expect(screen.getByLabelText('After')).toHaveFocus();
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('listbox')).toBeNull();
    });

    it('stays open when the input loses focus to nothing', async () => {
        // Focus going nowhere, such as the window losing it, is not focus
        // leaving for another field.
        const user = userEvent.setup();
        renderBetweenFields();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        act(() => input.blur());

        expect(input).not.toHaveFocus();
        expect(input).toHaveAttribute('aria-expanded', 'true');
        expect(screen.getByRole('listbox')).toBeInTheDocument();
    });

    // The filter buttons are inside the control, so focus on one leaves the
    // list open, and the Escape pressed there is the list's, as it is in the
    // input. Unhandled, it would reach the editor with the list still on
    // screen and cancel the edit.
    it('closes the list on Escape from a filter button, and marks and stops that Escape', async () => {
        const user = userEvent.setup();
        const { parentEscapes, windowEscapes, marks } = renderBetweenFields(vi.fn(), WITH_ACCOUNT);
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        await user.tab();
        expect(screen.getByRole('button', { name: /^All/ })).toHaveFocus();

        await user.keyboard('{Escape}');

        expect(screen.queryByRole('listbox')).toBeNull();
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(marks()).toEqual([true]);
        expect(parentEscapes).toEqual([]);
        expect(windowEscapes).toEqual([]);
    });

    it('gives focus back to the input, closed, so the next Escape is the editor\'s', async () => {
        // The button goes away with the list. Left there, focus would fall to
        // the page and the next Escape would never reach the editor. The input
        // opens its list on focus, and taking focus back must not reopen it.
        const user = userEvent.setup();
        const { parentEscapes, windowEscapes, marks } = renderBetweenFields(vi.fn(), WITH_ACCOUNT);
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        await user.tab();
        await user.keyboard('{Escape}');

        expect(input).toHaveFocus();
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('listbox')).toBeNull();

        await user.keyboard('{Escape}');
        expect(marks()).toEqual([true, false]);
        expect(parentEscapes).toEqual([{ defaultPrevented: false }]);
        expect(windowEscapes).toHaveLength(1);
    });

    it('keeps the typed search and the filter when focus comes back from a filter button', async () => {
        // A move inside the control is not a fresh visit to the field: the
        // search and the filter chosen on the way are still the user's.
        const user = userEvent.setup();
        renderBetweenFields(vi.fn(), WITH_ACCOUNT);
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        await user.type(input, 'ga');
        await user.tab();
        await user.tab();
        await user.tab();
        expect(screen.getByRole('button', { name: /^Categories/ })).toHaveFocus();
        await user.keyboard(' ');
        expect(screen.getByRole('button', { name: /^Categories/ })).toHaveAttribute('aria-pressed', 'true');

        await user.tab({ shift: true });
        await user.tab({ shift: true });
        await user.tab({ shift: true });

        expect(input).toHaveFocus();
        expect(input).toHaveValue('ga');
        expect(screen.getByRole('button', { name: /^Categories/ })).toHaveAttribute('aria-pressed', 'true');
        expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual(['Auto', 'Gas']);
    });

    it('keeps the typed search after a press on a group heading', async () => {
        // The press does not move focus, and the click lands inside the
        // field's label, which focuses a field that already has it.
        const user = userEvent.setup();
        renderBetweenFields();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        await user.type(input, 'ga');

        await user.click(screen.getByText('Expense'));

        expect(input).toHaveFocus();
        expect(input).toHaveValue('ga');
        expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual(['Auto', 'Gas']);
    });

    it('still closes the list on Escape in the input, marked and stopped', async () => {
        // Marked, for anything further out that reads it; stopped, so neither a
        // handler on an ancestor nor one on the window hears it at all.
        const user = userEvent.setup();
        const { parentEscapes, windowEscapes, marks } = renderBetweenFields(vi.fn(), WITH_ACCOUNT);
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.keyboard('{Escape}');

        expect(input).toHaveFocus();
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(marks()).toEqual([true]);
        expect(parentEscapes).toEqual([]);
        expect(windowEscapes).toEqual([]);

        await user.keyboard('{Escape}');
        expect(marks()).toEqual([true, false]);
        expect(parentEscapes).toEqual([{ defaultPrevented: false }]);
        expect(windowEscapes).toHaveLength(1);
    });
});

describe('AccountCategoryPicker inside a dialog', () => {
    // The dialog's panel can take focus, so a press on a part of the list that
    // cannot (a group heading, the filter bar) moves focus to the panel rather
    // than to nothing. That is still a press inside the control, not focus
    // leaving it: closing there left focus on the panel, and the next Escape
    // closed the dialog.
    function renderInDialog(accounts: AccountSummary[] = CATS, onChangeId = vi.fn()) {
        const onClose = vi.fn();
        render(
            <Modal open onClose={onClose} ariaLabel="Dialog">
                <AccountCategoryPicker
                    accounts={accounts}
                    isEligible={isAnything}
                    valueId={null}
                    onChangeId={onChangeId}
                    ariaLabel="Category"
                />
                <input aria-label="After" />
            </Modal>,
        );
        return { onClose, onChangeId };
    }

    it('keeps the list open after a press on a group heading, and its Escape stays the list\'s', async () => {
        const user = userEvent.setup();
        const { onClose } = renderInDialog();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.click(screen.getByText('Expense'));

        expect(screen.getByRole('listbox')).toBeInTheDocument();
        expect(input).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(screen.queryByRole('listbox')).toBeNull();
        expect(onClose).not.toHaveBeenCalled();

        await user.keyboard('{Escape}');
        expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('keeps the list open after a press on the filter bar', async () => {
        const user = userEvent.setup();
        renderInDialog(WITH_ACCOUNT);
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);
        const bar = screen.getByRole('group', { name: 'Show' }).parentElement!;

        await user.click(bar);

        expect(screen.getByRole('listbox')).toBeInTheDocument();
        expect(input).toHaveFocus();
    });

    it('commits a click on an option', async () => {
        const user = userEvent.setup();
        const { onChangeId } = renderInDialog();
        const input = screen.getByRole('combobox', { name: 'Category' });
        await user.click(input);

        await user.click(screen.getByRole('option', { name: 'Gas' }));

        expect(onChangeId).toHaveBeenCalledWith('gas');
        expect(input).toHaveAttribute('aria-expanded', 'false');
    });

    it('still closes the list when Tab leaves it for the next field', async () => {
        // The dialog's Tab trap skips elements with no offsetParent, which is
        // every element in jsdom; without this it sends Tab to the panel.
        const laidOut = vi.spyOn(HTMLElement.prototype, 'offsetParent', 'get')
            .mockImplementation(function (this: HTMLElement) { return this.parentElement; });
        try {
            const user = userEvent.setup();
            renderInDialog();
            const input = screen.getByRole('combobox', { name: 'Category' });
            await user.click(input);
            expect(screen.getByRole('listbox')).toBeInTheDocument();

            await user.tab();

            expect(screen.getByLabelText('After')).toHaveFocus();
            expect(input).toHaveAttribute('aria-expanded', 'false');
            expect(screen.queryByRole('listbox')).toBeNull();
        } finally {
            laidOut.mockRestore();
        }
    });
});
