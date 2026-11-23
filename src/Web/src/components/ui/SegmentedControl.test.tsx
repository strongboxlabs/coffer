import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { SegmentedControl, type SegmentedOption } from './SegmentedControl';

type V = 'a' | 'b';
const OPTIONS: readonly SegmentedOption<V>[] = [
    { value: 'a', label: 'Alpha' },
    { value: 'b', label: 'Beta', hint: '⌥2' },
];

describe('SegmentedControl', () => {
    it('is a named group of toggle buttons, the chosen one pressed', () => {
        render(<SegmentedControl label="View" options={OPTIONS} value="a" onChange={vi.fn()} />);
        expect(screen.getByRole('group', { name: 'View' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Alpha' })).toHaveAttribute('aria-pressed', 'true');
        expect(screen.getByRole('button', { name: /Beta/ })).toHaveAttribute('aria-pressed', 'false');
    });

    it('shows a focus ring on every segment', () => {
        render(<SegmentedControl label="View" options={OPTIONS} value="a" onChange={vi.fn()} />);
        for (const b of screen.getAllByRole('button')) {
            expect(b.className).toContain('focus-visible:ring-2');
        }
    });

    it('chooses on click and on the keyboard', async () => {
        const onChange = vi.fn();
        render(<SegmentedControl label="View" options={OPTIONS} value="a" onChange={onChange} />);
        await userEvent.click(screen.getByRole('button', { name: /Beta/ }));
        expect(onChange).toHaveBeenLastCalledWith('b');

        screen.getByRole('button', { name: 'Alpha' }).focus();
        await userEvent.keyboard('{Enter}');
        expect(onChange).toHaveBeenLastCalledWith('a');
    });

    it('with keepFocus, a press chooses once and leaves focus in the input', () => {
        const onChange = vi.fn();
        render(
            <>
                <input aria-label="Search" />
                <SegmentedControl label="Show" options={OPTIONS} value="a" onChange={onChange} keepFocus />
            </>,
        );
        const input = screen.getByRole('textbox', { name: 'Search' });
        input.focus();
        const beta = screen.getByRole('button', { name: /Beta/ });

        expect(fireEvent.mouseDown(beta)).toBe(false); // default prevented: no focus move
        fireEvent.click(beta, { detail: 1 });
        expect(onChange).toHaveBeenCalledTimes(1);
        expect(onChange).toHaveBeenCalledWith('b');
        expect(document.activeElement).toBe(input);
    });

    // ADR-0023 §L: a choice whose request is in flight keeps focus and
    // refuses to be made, from a press, Enter or Space.
    it('refuses every choice while pending, and keeps focus on the option', async () => {
        const onChange = vi.fn();
        render(<SegmentedControl label="Columns" options={OPTIONS} value="a" onChange={onChange} pending />);
        const beta = screen.getByRole('button', { name: /Beta/ });
        expect(beta).toHaveAttribute('aria-busy', 'true');
        expect(beta).toHaveAttribute('aria-disabled', 'true');
        expect(beta).not.toBeDisabled();
        await userEvent.click(beta);
        beta.focus();
        await userEvent.keyboard('{Enter} ');
        expect(onChange).not.toHaveBeenCalled();
        expect(beta).toHaveFocus();
    });

    it('refuses the press that chooses with keepFocus while pending', () => {
        const onChange = vi.fn();
        render(<SegmentedControl label="Show" options={OPTIONS} value="a" onChange={onChange} keepFocus pending />);
        const beta = screen.getByRole('button', { name: /Beta/ });
        expect(fireEvent.mouseDown(beta)).toBe(false);
        fireEvent.click(beta, { detail: 0 });
        expect(onChange).not.toHaveBeenCalled();
    });
});
