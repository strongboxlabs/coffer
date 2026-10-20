import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { RegisterSortMenu } from './RegisterSortMenu';
import { DEFAULT_SORT, type RegisterSortState } from './registerSort';

function openMenu(investment: boolean, sort: RegisterSortState = DEFAULT_SORT) {
    const onChange = vi.fn();
    render(<RegisterSortMenu sort={sort} onChange={onChange} investment={investment} />);
    fireEvent.click(screen.getByRole('button', { name: /Sort:/ }));
    return { onChange, listbox: screen.getByRole('listbox') };
}

describe('RegisterSortMenu', () => {
    it('offers only the shared columns on a bank register', () => {
        const { listbox } = openMenu(false);
        for (const col of ['Date', 'Amount', 'Payee', 'Category']) {
            expect(within(listbox).getByRole('option', { name: new RegExp(col) })).toBeTruthy();
        }
        // The investment-only columns read off the security leg — never offered
        // on a bank register.
        for (const col of ['Security', 'Shares', 'Price', 'Action']) {
            expect(within(listbox).queryByRole('option', { name: new RegExp(col) })).toBeNull();
        }
    });

    it('adds the investment-only columns on an investment register', () => {
        const { listbox } = openMenu(true);
        for (const col of ['Security', 'Shares', 'Price', 'Action']) {
            expect(within(listbox).getByRole('option', { name: new RegExp(col) })).toBeTruthy();
        }
    });

    it('a numeric column starts descending (largest first)', () => {
        const { onChange } = openMenu(false); // active = date desc
        fireEvent.click(screen.getByRole('option', { name: /Amount/ }));
        expect(onChange).toHaveBeenCalledWith({ column: 'amount', dir: 'desc' });
    });

    it('a text column starts ascending (A→Z)', () => {
        const { onChange } = openMenu(false);
        fireEvent.click(screen.getByRole('option', { name: /Payee/ }));
        expect(onChange).toHaveBeenCalledWith({ column: 'payee', dir: 'asc' });
    });

    it('re-picking the active column toggles the direction', () => {
        const { onChange } = openMenu(false, { column: 'amount', dir: 'desc' });
        fireEvent.click(screen.getByRole('option', { name: /Amount/ }));
        expect(onChange).toHaveBeenCalledWith({ column: 'amount', dir: 'asc' });
    });

    it('names the counterparty column ACCOUNT on a category register', () => {
        // The third time this defect shipped, after the column header and the
        // editor field: on a category's register the counterparty is a money
        // account, and the filter and chips beside this menu always knew it.
        // The menu was the one control in the bar never handed the kind.
        const onChange = vi.fn();
        render(
            <RegisterSortMenu
                sort={{ column: 'category', dir: 'asc' }}
                onChange={onChange}
                investment={false}
                counterpartyKind="account"
            />,
        );

        // The trigger too, not only the option — sorted by it, the bar read
        // "Sort: Category" on a register with no categories in it.
        const trigger = screen.getByRole('button', { name: /Sort:/ });
        expect(trigger).toHaveTextContent('Account');
        fireEvent.click(trigger);
        const listbox = screen.getByRole('listbox');
        expect(within(listbox).getByRole('option', { name: /Account/ })).toBeTruthy();

        // Anchored by the two positives above: the wrong word is nowhere.
        expect(within(listbox).queryByRole('option', { name: /Category/ })).toBeNull();
        expect(trigger).not.toHaveTextContent('Category');

        // Same column, same wire value — only the word changed.
        fireEvent.click(within(listbox).getByRole('option', { name: /Account/ }));
        expect(onChange).toHaveBeenCalledWith({ column: 'category', dir: 'desc' });
    });
});
