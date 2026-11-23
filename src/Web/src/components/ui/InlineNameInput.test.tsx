import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { InlineNameInput } from './InlineNameInput';

function setup(initial = '') {
    const onCommit = vi.fn();
    const onCancel = vi.fn();
    render(
        <InlineNameInput label="Folder name" initial={initial} onCommit={onCommit} onCancel={onCancel} />,
    );
    return { input: screen.getByRole('textbox', { name: 'Folder name' }), onCommit, onCancel };
}

describe('InlineNameInput', () => {
    it('opens focused with its text selected', () => {
        const { input } = setup('Old name');
        expect(input).toHaveFocus();
        expect((input as HTMLInputElement).selectionStart).toBe(0);
        expect((input as HTMLInputElement).selectionEnd).toBe('Old name'.length);
    });

    it('commits the trimmed name on Enter, once', async () => {
        const { input, onCommit, onCancel } = setup();
        await userEvent.setup().type(input, '  New name  {Enter}');
        fireEvent.blur(input);
        expect(onCommit).toHaveBeenCalledTimes(1);
        // 'key', so the caller knows to hand focus back.
        expect(onCommit).toHaveBeenCalledWith('New name', 'key');
        expect(onCancel).not.toHaveBeenCalled();
    });

    it('cancels on Escape', async () => {
        const first = setup('Kept');
        await userEvent.setup().type(first.input, '{Escape}');
        expect(first.onCancel).toHaveBeenCalledTimes(1);
        expect(first.onCancel).toHaveBeenCalledWith('key');
        expect(first.onCommit).not.toHaveBeenCalled();
    });

    it('commits on blur, and says so', async () => {
        const typed = setup();
        await userEvent.setup().type(typed.input, 'Typed');
        fireEvent.blur(typed.input);
        // 'blur': focus is already going where the reader sent it.
        expect(typed.onCommit).toHaveBeenCalledWith('Typed', 'blur');
    });

    it('treats a blank name as a cancel', () => {
        const blank = setup('   ');
        fireEvent.blur(blank.input);
        expect(blank.onCancel).toHaveBeenCalledTimes(1);
        expect(blank.onCancel).toHaveBeenCalledWith('blur');
        expect(blank.onCommit).not.toHaveBeenCalled();
    });

    it('keeps its keys from the register behind it', () => {
        const outer = vi.fn();
        document.addEventListener('keydown', outer);
        try {
            const { input } = setup();
            fireEvent.keyDown(input, { key: 'n' });
            fireEvent.keyDown(input, { key: 'ArrowDown' });
            expect(outer).not.toHaveBeenCalled();
        } finally {
            document.removeEventListener('keydown', outer);
        }
    });
});
