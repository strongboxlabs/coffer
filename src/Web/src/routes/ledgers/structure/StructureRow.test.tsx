import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';

import { StructureBadge, StructureRow } from './StructureRow';

// The row the three lists of Structure are built from (ADR-0111 D4). What the tabs share is
// what is tested here: the columns that hold still between tabs, and the menu's two doors.

function renderRow(props: Parameters<typeof StructureRow>[0]) {
    return render(<ul><StructureRow {...props} /></ul>);
}

describe('StructureRow columns', () => {
    it('holds the amount column open when a list has no amount, so the count does not move between tabs', () => {
        const withAmount = renderRow({ name: 'Groceries', count: '3 transactions', amount: '$10.00' });
        const withoutAmount = renderRow({ name: 'Trip', count: '3 transactions' });

        // The same two fixed-width cells, in the same order, whether or not there is an amount.
        for (const { container } of [withAmount, withoutAmount]) {
            const li = container.querySelector('li')!;
            const widths = [...li.children]
                .map((c) => c.className.match(/w-fixed-\d+px/)?.[0])
                .filter(Boolean);
            expect(widths).toEqual(['w-fixed-128px', 'w-fixed-96px']);
        }
    });

    it('leaves the secondary column out when a list has none, and gives it a cell when it does', () => {
        const { container: without } = renderRow({ name: 'Trip' });
        const { container: withText } = renderRow({ name: 'Checking', secondary: 'First Bank · USD' });
        expect(without.querySelector('.w-fixed-160px')).toBeNull();
        expect(withText.querySelector('.w-fixed-160px')).toHaveTextContent('First Bank · USD');
    });

    it('draws a badge the same way wherever it is used', () => {
        renderRow({ name: 'Old', badges: <StructureBadge>Inactive</StructureBadge> });
        expect(screen.getByText('Inactive')).toHaveClass('uppercase');
    });
});

describe('StructureRow menu', () => {
    it('opens at the pointer on a right-click and at the kebab from its button, for the same row', () => {
        const onOpen = vi.fn();
        renderRow({ name: 'Groceries', menu: { label: 'Actions for Groceries', onOpen } });

        fireEvent.contextMenu(screen.getByRole('listitem'), { clientX: 40, clientY: 50 });
        expect(onOpen).toHaveBeenLastCalledWith({ x: 40, y: 50 });

        fireEvent.click(screen.getByRole('button', { name: 'Actions for Groceries' }));
        expect(onOpen).toHaveBeenCalledTimes(2);
    });

    it('gives a row with no actions a spacer where the button would be, and no menu', () => {
        const { container } = renderRow({ name: 'Reserved', menu: 'none' });

        expect(screen.queryByRole('button')).not.toBeInTheDocument();
        expect(container.querySelector('.size-control-28px')).not.toBeNull();
        // A right-click on it is the browser's, not a menu that has nothing in it.
        const prevented = fireEvent.contextMenu(screen.getByRole('listitem'));
        expect(prevented).toBe(true);
    });

    it('gives a reader who cannot edit no button, no spacer and no menu', () => {
        const { container } = renderRow({ name: 'Groceries' });

        expect(screen.queryByRole('button')).not.toBeInTheDocument();
        expect(container.querySelector('.size-control-28px')).toBeNull();
        expect(fireEvent.contextMenu(screen.getByRole('listitem'))).toBe(true);
    });
});
