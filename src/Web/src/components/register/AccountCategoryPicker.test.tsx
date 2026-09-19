import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
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
    // The panel is ~286px at full height and position:absolute, so an ancestor
    // with overflow-y:auto CLIPS it. That coupling is deliberate — the panel
    // can never be visible while its input is not, which is what stops an
    // opaque dropdown floating over unrelated chrome after a scroll, still
    // accepting clicks into a field the user can no longer see. So a container
    // with limited room makes the panel FIT rather than making it ESCAPE.
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
        const list = container.querySelector('ul');
        expect(list?.className).toContain('max-h-fixed-160px');
        expect(list?.className).not.toContain('max-h-fixed-256px');
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
        expect(container.querySelector('ul')?.className).toContain('max-h-fixed-256px');
    });
});

describe('onOpenChange', () => {
    it('does not fire on mount, only on a real open', async () => {
        // A container that scrolls itself on every notification would jump on
        // first render. A picker that has never been opened has not "closed".
        const onOpenChange = vi.fn();
        const user = userEvent.setup();
        render(
            <AccountCategoryPicker
                accounts={CATS}
                isEligible={isCategory}
                valueId={null}
                onChangeId={vi.fn()}
                ariaLabel="Category"
                onOpenChange={onOpenChange}
            />,
        );
        expect(onOpenChange).not.toHaveBeenCalled();

        await user.click(screen.getByRole('combobox', { name: 'Category' }));
        expect(onOpenChange).toHaveBeenCalledWith(true);
    });

    it('reports the close too', async () => {
        const onOpenChange = vi.fn();
        const user = userEvent.setup();
        render(
            <AccountCategoryPicker
                accounts={CATS}
                isEligible={isCategory}
                valueId={null}
                onChangeId={vi.fn()}
                ariaLabel="Category"
                onOpenChange={onOpenChange}
            />,
        );
        const box = screen.getByRole('combobox', { name: 'Category' });
        await user.click(box);
        onOpenChange.mockClear();
        await user.keyboard('{Escape}');
        expect(onOpenChange).toHaveBeenCalledWith(false);
    });
});
