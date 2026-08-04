import { useState } from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { RecurrenceBuilder, defaultSchedule, type ScheduleValue } from './RecurrenceBuilder';

// Controlled wrapper: holds the canonical ScheduleValue in useState and
// re-renders the builder on each onChange, mirroring how the reminder dialog
// owns the value. A `seen` ref exposes the latest emitted value to assertions.
function Harness({ initial, seen }: { initial: ScheduleValue; seen: { value: ScheduleValue } }) {
    const [value, setValue] = useState<ScheduleValue>(initial);
    seen.value = value;
    return (
        <RecurrenceBuilder
            value={value}
            onChange={(next) => { seen.value = next; setValue(next); }}
        />
    );
}

function renderBuilder(start = '2026-06-16') {
    const seen = { value: defaultSchedule(start) };
    render(<Harness initial={seen.value} seen={seen} />);
    return seen;
}

describe('RecurrenceBuilder', () => {
    it('defaultSchedule: monthly on the start date day-of-month, no end, manual', () => {
        const s = defaultSchedule('2026-06-16');
        expect(s).toEqual({
            recurrence: { freq: 'monthly', interval: 1, weekdays: [], monthDay: 16 },
            startDate: '2026-06-16',
            endDate: null,
            autoCommitDaysBefore: null,
        });
    });

    it('switching to Weekly reveals weekday toggles and emits a WEEKLY recurrence', async () => {
        const seen = renderBuilder();
        // Weekday toggles are absent under the default monthly freq.
        expect(screen.queryByRole('group', { name: 'Weekdays' })).not.toBeInTheDocument();

        await userEvent.selectOptions(screen.getByLabelText('Frequency'), 'weekly');

        expect(screen.getByRole('group', { name: 'Weekdays' })).toBeInTheDocument();
        expect(seen.value.recurrence.freq).toBe('weekly');
        // Seeded with the start date's weekday (2026-06-16 is a Tuesday → TU).
        expect(seen.value.recurrence.weekdays).toEqual(['TU']);

        // Add another day; canonical SU..SA order is preserved.
        await userEvent.click(screen.getByRole('button', { name: 'MO' }));
        expect(seen.value.recurrence.weekdays).toEqual(['MO', 'TU']);
    });

    it('Monthly "Last day" emits monthDay:last and disables the number input', async () => {
        const seen = renderBuilder();
        const lastDay = screen.getByRole('checkbox', { name: 'Last day' });
        await userEvent.click(lastDay);

        expect(seen.value.recurrence.monthDay).toBe('last');
        expect(screen.getByLabelText('Day of month')).toBeDisabled();
    });

    it('clicking "On" seeds a default end date one year out (was a dead control)', async () => {
        const seen = renderBuilder('2026-06-16');
        expect(seen.value.endDate).toBeNull();

        await userEvent.click(screen.getByRole('radio', { name: 'On' }));

        // The radio used to be a no-op; now it seeds a concrete date so the
        // choice registers and the date field is populated.
        expect(seen.value.endDate).toBe('2027-06-16');
        expect(screen.getByRole('radio', { name: 'On' })).toBeChecked();
    });

    it('the end date input sets a specific end date', async () => {
        const seen = renderBuilder();
        expect(seen.value.endDate).toBeNull();

        await userEvent.type(screen.getByLabelText('End date'), '2027-01-31');

        expect(seen.value.endDate).toBe('2027-01-31');
    });

    it('weekly refuses to drop the last weekday (no empty BYDAY)', async () => {
        const seen = renderBuilder('2026-06-16'); // a Tuesday
        await userEvent.selectOptions(screen.getByLabelText('Frequency'), 'weekly');
        expect(seen.value.recurrence.weekdays).toEqual(['TU']);

        // Deselecting the only remaining day is refused.
        await userEvent.click(screen.getByRole('button', { name: 'TU' }));
        expect(seen.value.recurrence.weekdays).toEqual(['TU']);

        // With two selected, either can still be removed.
        await userEvent.click(screen.getByRole('button', { name: 'MO' }));
        expect(seen.value.recurrence.weekdays).toEqual(['MO', 'TU']);
        await userEvent.click(screen.getByRole('button', { name: 'TU' }));
        expect(seen.value.recurrence.weekdays).toEqual(['MO']);
    });

    it('shows a short-month hint only when day-of-month is 29-31', async () => {
        renderBuilder('2026-06-16'); // default monthly on the 16th -> no hint
        expect(screen.queryByText(/skipped in shorter months/i)).not.toBeInTheDocument();

        const dayInput = screen.getByLabelText('Day of month');
        await userEvent.clear(dayInput);
        await userEvent.type(dayInput, '31');

        expect(screen.getByText(/skipped in shorter months/i)).toBeInTheDocument();
    });

    it('Auto-post + N days before emits autoCommitDaysBefore:N', async () => {
        const seen = renderBuilder();
        expect(seen.value.autoCommitDaysBefore).toBeNull();

        await userEvent.click(screen.getByRole('radio', { name: 'Auto-post' }));
        const days = screen.getByLabelText('Days before due');
        await userEvent.clear(days);
        await userEvent.type(days, '3');

        expect(seen.value.autoCommitDaysBefore).toBe(3);
    });

    it('renders the live preview line', () => {
        renderBuilder();
        // Default is monthly on the 16th, from the start date.
        expect(screen.getByText(/Monthly on the 16th · from 2026-06-16/)).toBeInTheDocument();
    });
});
