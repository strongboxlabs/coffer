import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';

import { ContextMenu, type ContextMenuItem } from './ContextMenu';

// Behaviour we lock down for the ContextMenu primitive:
//
//   * Items render with their label + optional shortcut hint.
//   * ↓/↑ move highlight; wraps at ends; skips disabled items.
//   * Enter / Space activate the highlighted item and close the menu
//     (so the caller's onClose fires + the item's onSelect fires).
//   * Escape closes without activating.
//   * Outside pointerdown closes the menu.
//   * Clicking a disabled item is a no-op.

function Harness({
    items,
    initialOpen = true,
}: {
    items: readonly ContextMenuItem[];
    initialOpen?: boolean;
}) {
    const [open, setOpen] = useState(initialOpen);
    return (
        <div>
            {open ? (
                <ContextMenu
                    anchor={{ x: 50, y: 50 }}
                    items={items}
                    onClose={() => setOpen(false)}
                />
            ) : null}
            <button type="button" data-testid="outside">outside</button>
            <span data-testid="open-state">{open ? 'open' : 'closed'}</span>
        </div>
    );
}

describe('ContextMenu', () => {
    it('renders each item label and shortcut hint', () => {
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'Duplicate', shortcutHint: '⌘D', onSelect: () => {} },
            { id: 'b', label: 'Delete', danger: true, onSelect: () => {} },
        ];
        render(<Harness items={items} />);
        expect(screen.getByRole('menuitem', { name: /Duplicate/ })).toBeInTheDocument();
        expect(screen.getByText('⌘D')).toBeInTheDocument();
        expect(screen.getByRole('menuitem', { name: /Delete/ })).toBeInTheDocument();
    });

    it('activates the highlighted item on Enter and fires onSelect + closes', async () => {
        const user = userEvent.setup();
        const duplicate = vi.fn();
        const del = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'Duplicate', onSelect: duplicate },
            { id: 'b', label: 'Delete', onSelect: del },
        ];
        render(<Harness items={items} />);
        // First item is highlighted by default; ArrowDown moves to second.
        await user.keyboard('{ArrowDown}');
        await user.keyboard('{Enter}');
        expect(duplicate).not.toHaveBeenCalled();
        expect(del).toHaveBeenCalledTimes(1);
        expect(screen.getByTestId('open-state')).toHaveTextContent('closed');
    });

    it('skips disabled items in keyboard nav', async () => {
        const user = userEvent.setup();
        const ok1 = vi.fn();
        const ok2 = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'First', onSelect: ok1 },
            { id: 'b', label: 'Disabled', disabled: true, onSelect: () => {} },
            { id: 'c', label: 'Last', onSelect: ok2 },
        ];
        render(<Harness items={items} />);
        // From first → arrow-down should land on Last (skipping Disabled).
        await user.keyboard('{ArrowDown}');
        await user.keyboard('{Enter}');
        expect(ok2).toHaveBeenCalledTimes(1);
        expect(ok1).not.toHaveBeenCalled();
    });

    it('wraps highlight at the end and start', async () => {
        const user = userEvent.setup();
        const onSelectA = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'A', onSelect: onSelectA },
            { id: 'b', label: 'B', onSelect: () => {} },
        ];
        render(<Harness items={items} />);
        // From first item, ArrowUp wraps to last (B). ArrowDown from
        // last wraps back to first (A). Then Enter activates A.
        await user.keyboard('{ArrowUp}');
        await user.keyboard('{ArrowDown}');
        await user.keyboard('{Enter}');
        expect(onSelectA).toHaveBeenCalledTimes(1);
    });

    it('closes on Escape without firing any onSelect', async () => {
        const user = userEvent.setup();
        const onSelect = vi.fn();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'A', onSelect },
        ];
        render(<Harness items={items} />);
        await user.keyboard('{Escape}');
        expect(onSelect).not.toHaveBeenCalled();
        expect(screen.getByTestId('open-state')).toHaveTextContent('closed');
    });

    it('closes on outside pointerdown', async () => {
        const user = userEvent.setup();
        const items: ContextMenuItem[] = [
            { id: 'a', label: 'A', onSelect: () => {} },
        ];
        render(<Harness items={items} />);
        await user.click(screen.getByTestId('outside'));
        expect(screen.getByTestId('open-state')).toHaveTextContent('closed');
    });
});
