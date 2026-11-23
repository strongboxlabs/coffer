import { describe, expect, it } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';

import { RegisterScrollSurface } from './RegisterScrollSurface';

// The row list is ringed for keyboard focus only (ADR-0023 §L). A press on a
// row focuses the list, and a browser rings focus a pointer gave once the next
// key is pressed; focus a press gave the list is marked so it shows no ring.

function Surface() {
    return (
        <RegisterScrollSurface scrollRef={() => {}}>
            <div role="row">
                <span>Row text</span>
                <button type="button">Status</button>
            </div>
        </RegisterScrollSurface>
    );
}

const list = () => screen.getByRole('grid', { name: 'Transactions' });

describe('RegisterScrollSurface: the ring', () => {
    it('rings keyboard focus only, never focus a press gave', () => {
        render(<Surface />);
        expect(list().className).toContain('not-data-[pressed]:focus-visible:ring-2');
    });

    it('marks focus a press on a row gave the list, and keeps the mark through the keys that follow', () => {
        render(<Surface />);
        fireEvent.pointerDown(screen.getByText('Row text'));
        act(() => list().focus());
        fireEvent.pointerUp(window);

        expect(list()).toHaveAttribute('data-pressed');
        fireEvent.keyDown(list(), { key: 'ArrowDown' });
        expect(list()).toHaveAttribute('data-pressed');
    });

    it('drops the mark when the list loses focus, so focus the keyboard gives it later is ringed', () => {
        render(<Surface />);
        fireEvent.pointerDown(screen.getByText('Row text'));
        act(() => list().focus());
        fireEvent.pointerUp(window);
        act(() => screen.getByRole('button', { name: 'Status' }).focus());
        expect(list()).not.toHaveAttribute('data-pressed');

        act(() => list().focus());

        expect(list()).not.toHaveAttribute('data-pressed');
    });

    it('marks nothing when a press gave focus to a control in a row, and the list is focused from the keyboard later', () => {
        render(<Surface />);
        const status = screen.getByRole('button', { name: 'Status' });
        fireEvent.pointerDown(status);
        act(() => status.focus());
        fireEvent.pointerUp(window);

        act(() => list().focus());

        expect(list()).not.toHaveAttribute('data-pressed');
    });
});
