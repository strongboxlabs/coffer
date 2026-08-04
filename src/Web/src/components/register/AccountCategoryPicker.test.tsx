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
