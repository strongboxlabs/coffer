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
});
