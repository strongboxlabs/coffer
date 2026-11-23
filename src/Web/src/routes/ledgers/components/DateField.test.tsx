import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';

import { DateField } from './DateField';

/**
 * Unit tests for the date input shared by the transaction editors. Testing the
 * keyboard shortcuts here rather than by driving the whole editor is the point
 * of the component: `t` / `y` / `+` / `-` are the behaviour, and this is where
 * they live.
 *
 * `fireEvent.change` rather than `userEvent.type`: native `type="date"` inputs do
 * not accept synthetic per-character typing reliably, so a typed value can arrive
 * empty.
 */

function setup(initial = '') {
    const onChange = vi.fn();
    const { rerender } = render(
        <DateField label="Date" value={initial} onChange={onChange} />,
    );
    const input = screen.getByLabelText('Date');
    return { input, onChange, rerender };
}

describe('DateField', () => {
    it('renders the input alone when bare, still named and still keyed', () => {
        const onChange = vi.fn();
        const { container } = render(
            <DateField bare label="Tax date" id="tax" value="2026-03-04" onChange={onChange} />,
        );
        const input = screen.getByLabelText('Tax date');
        expect(container.firstChild).toBe(input);
        expect(container.querySelector('label')).toBeNull();
        expect(input).toHaveAttribute('id', 'tax');
        fireEvent.keyDown(input, { key: '+' });
        expect(onChange).toHaveBeenLastCalledWith('2026-03-05');
    });

    it('reports typed dates through onChange', () => {
        const { input, onChange } = setup();
        fireEvent.change(input, { target: { value: '2026-03-04' } });
        expect(onChange).toHaveBeenCalledWith('2026-03-04');
    });

    it('shifts a day forward on + and back on -', () => {
        const { input, onChange } = setup('2026-03-04');

        fireEvent.keyDown(input, { key: '+' });
        expect(onChange).toHaveBeenLastCalledWith('2026-03-05');

        fireEvent.keyDown(input, { key: '-' });
        expect(onChange).toHaveBeenLastCalledWith('2026-03-03');
    });

    it('crosses a month boundary correctly', () => {
        const { input, onChange } = setup('2026-02-28');
        fireEvent.keyDown(input, { key: '+' });
        // 2026 is not a leap year, so the 28th is the last day of February.
        expect(onChange).toHaveBeenLastCalledWith('2026-03-01');
    });

    it('jumps to today on t and yesterday on y', () => {
        const { input, onChange } = setup('2020-01-01');

        fireEvent.keyDown(input, { key: 't' });
        const today = onChange.mock.calls.at(-1)![0] as string;
        expect(today).toMatch(/^\d{4}-\d{2}-\d{2}$/);

        fireEvent.keyDown(input, { key: 'y' });
        const yesterday = onChange.mock.calls.at(-1)![0] as string;
        expect(Date.parse(today) - Date.parse(yesterday)).toBe(86_400_000);
    });

    it('shifts from today when the value is empty', () => {
        const { input, onChange } = setup('');
        // The tax-date field starts empty, so +/- must not produce NaN dates.
        fireEvent.keyDown(input, { key: '+' });
        expect(onChange.mock.calls.at(-1)![0]).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    });

    it('is labelled for assistive tech and for tests', () => {
        setup();
        // aria-label is what lets both a screen reader and getByLabelText find
        // it; the visible <span> is decorative styling, not a bound label.
        expect(screen.getByLabelText('Date')).toHaveAttribute('type', 'date');
    });
});
