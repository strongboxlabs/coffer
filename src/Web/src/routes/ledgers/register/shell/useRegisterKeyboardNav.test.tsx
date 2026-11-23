import { describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';

import { ContextMenu } from '@/components/ui/ContextMenu';
import type { IndexBucketDto } from '@/lib/types/register';
import { RegisterDateJumpPopover } from '../RegisterDateJumpPopover';
import { RegisterStatusMenu } from './RegisterStatusMenu';
import { useRegisterKeyboardNav } from './useRegisterKeyboardNav';

// The register's document-level shortcuts (arrows, Enter, N) act on the rows.
// A key pressed in a dialog, a menu or a list over the register is that
// popup's: the rows are behind it, so N would open a new row under it and an
// arrow would move the row focus out of sight.

const ROWS = ['row-1', 'row-2', 'row-3'];

const BUCKETS: IndexBucketDto[] = [
    { yearMonth: '2026-01', count: 1, sampleHeaderId: 'header-1' },
    { yearMonth: '2026-02', count: 1, sampleHeaderId: 'header-2' },
];

function Register({ onCreate, onEnterRow, children }: {
    onCreate: () => void;
    onEnterRow: (id: string) => void;
    children?: ReactNode;
}) {
    const { focusedRowId } = useRegisterKeyboardNav({
        rows: ROWS,
        getRowId: (row) => row,
        onLoadNewer: () => {},
        onLoadOlder: () => {},
        scrollRowIntoView: () => {},
        enabled: true,
        onEnterRow: (id) => onEnterRow(id),
        onCreate,
    });
    return (
        <div>
            <output aria-label="Focused row">{focusedRowId ?? 'none'}</output>
            <button type="button">On the page</button>
            {children}
        </div>
    );
}

function renderRegister(children?: ReactNode) {
    const onCreate = vi.fn();
    const onEnterRow = vi.fn();
    render(<Register onCreate={onCreate} onEnterRow={onEnterRow}>{children}</Register>);
    return { onCreate, onEnterRow };
}

const focusedRow = () => screen.getByRole('status', { name: 'Focused row' }).textContent;

describe('useRegisterKeyboardNav and popups', () => {
    it('acts on a key pressed on the page', async () => {
        // The control: the same keys, from a button on the page, drive the rows.
        const user = userEvent.setup();
        const { onCreate } = renderRegister();
        act(() => screen.getByRole('button', { name: 'On the page' }).focus());

        await user.keyboard('{ArrowDown}');
        expect(focusedRow()).toBe('row-1');

        await user.keyboard('n');
        expect(onCreate).toHaveBeenCalledTimes(1);
    });

    it('leaves a key pressed in a dialog to the dialog', async () => {
        const user = userEvent.setup();
        const { onCreate, onEnterRow } = renderRegister(
            <div role="dialog" aria-modal="true" aria-label="A dialog">
                <button type="button">In the dialog</button>
                <input type="checkbox" aria-label="Checkbox in the dialog" />
            </div>,
        );
        act(() => screen.getByRole('button', { name: 'On the page' }).focus());
        await user.keyboard('{ArrowDown}');
        expect(focusedRow()).toBe('row-1');

        act(() => screen.getByRole('button', { name: 'In the dialog' }).focus());
        await user.keyboard('{ArrowDown}{ArrowDown}{ArrowUp}n');
        // A checkbox is not a field, so Enter on one opens a row on the page.
        act(() => screen.getByRole('checkbox', { name: 'Checkbox in the dialog' }).focus());
        await user.keyboard('{Enter}');

        expect(focusedRow()).toBe('row-1');
        expect(onCreate).not.toHaveBeenCalled();
        expect(onEnterRow).not.toHaveBeenCalled();
    });

    it('leaves a key to a dialog when the control it was pressed on took itself away', () => {
        // Each control goes as its key goes down, the way React removes one
        // between its own listener and the document's; the dialog stays. Built
        // outside React, so nothing unmounts what the key already removed.
        const { onCreate } = renderRegister();
        const dialog = document.createElement('div');
        dialog.setAttribute('role', 'dialog');
        const controls = ['ArrowDown', 'n'].map((key) => {
            const control = document.createElement('button');
            control.addEventListener('keydown', () => control.remove());
            dialog.append(control);
            return [key, control] as const;
        });
        document.body.append(dialog);
        try {
            for (const [key, control] of controls) {
                fireEvent.keyDown(control, { key });
                expect(control.isConnected).toBe(false);
            }
        } finally {
            dialog.remove();
        }

        expect(focusedRow()).toBe('none');
        expect(onCreate).not.toHaveBeenCalled();
    });

    it('leaves a key pressed in a row\'s menu to the menu', async () => {
        // The menu moves between its items on the arrows; the row focus behind
        // it stays put.
        const user = userEvent.setup();
        const { onCreate } = renderRegister(
            <ContextMenu
                anchor={{ x: 10, y: 10 }}
                items={[
                    { id: 'a', label: 'First', onSelect: vi.fn() },
                    { id: 'b', label: 'Second', onSelect: vi.fn() },
                ]}
                onClose={vi.fn()}
                label="Row actions"
            />,
        );
        expect(screen.getByRole('menu', { name: 'Row actions' })).toHaveFocus();

        await user.keyboard('{ArrowDown}{ArrowDown}n');

        expect(focusedRow()).toBe('none');
        expect(onCreate).not.toHaveBeenCalled();
    });

    it('leaves a key pressed on the Show menu\'s options to the menu', async () => {
        const user = userEvent.setup();
        const { onCreate } = renderRegister(
            <RegisterStatusMenu statusFilter="all" onChange={vi.fn()} counts={null} />,
        );
        await user.click(screen.getByRole('button', { name: /Show:/ }));
        act(() => screen.getAllByRole('option')[1]!.focus());

        await user.keyboard('{ArrowDown}n');

        expect(focusedRow()).toBe('none');
        expect(onCreate).not.toHaveBeenCalled();
    });

    it('leaves a key pressed on the date-jump popover\'s buttons to the popover', async () => {
        const user = userEvent.setup();
        const { onCreate } = renderRegister(<RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />);
        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.keyboard('{Control>}j{/Control}');
        act(() => screen.getByRole('button', { name: 'Jump' }).focus());

        await user.keyboard('{ArrowDown}n');

        expect(screen.getByRole('dialog', { name: 'Jump to date' })).toBeInTheDocument();
        expect(focusedRow()).toBe('none');
        expect(onCreate).not.toHaveBeenCalled();
    });
});
