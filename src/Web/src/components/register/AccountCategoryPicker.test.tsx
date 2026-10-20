import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { AccountCategoryPicker } from './AccountCategoryPicker';
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
    // The panel is fixed to the viewport and closes on scroll, so it no longer
    // has to fit inside a scrolling ancestor. `compact` survives as a shorter
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
