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
            // Mig 220: a new reminder uses the amount as entered, not an estimate. The
            // deep-equal is kept rather than loosened to toMatchObject — it is what makes
            // adding a field to ScheduleValue a deliberate act rather than a silent one.
            estimateSampleCount: null,
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

describe('RecurrenceBuilder — estimated amounts (mig 220)', () => {
    /*
     * The control is offered only to a series that can actually use it. A split has no
     * single amount to estimate and a loan payment is computed from its terms, so both
     * are refused server-side; hiding the control is the courtesy that stops a user
     * choosing something that would be rejected.
     */
    it('offers the estimate option when the series is eligible', () => {
        render(
            <RecurrenceBuilder
                value={defaultSchedule('2026-06-16')}
                onChange={() => {}}
                estimateEligible
            />,
        );
        expect(screen.getByRole('radio', { name: /estimate from history/i })).toBeTruthy();
    });

    it('hides it when the series cannot estimate', () => {
        render(
            <RecurrenceBuilder
                value={defaultSchedule('2026-06-16')}
                onChange={() => {}}
                estimateEligible={false}
            />,
        );
        expect(screen.queryByRole('radio', { name: /estimate from history/i })).toBeNull();
    });

    /*
     * 3 is the default the maintainer specified: enough to smooth a variable bill, short
     * enough to follow a real change in it. Asserted because a default that silently
     * became 1 would turn "average of recent occurrences" into "repeat the last one".
     */
    it('defaults the sample size to 3 when estimation is switched on', async () => {
        const seen: ScheduleValue[] = [];
        render(
            <RecurrenceBuilder
                value={defaultSchedule('2026-06-16')}
                onChange={(next) => seen.push(next)}
                estimateEligible
            />,
        );

        await userEvent.click(screen.getByRole('radio', { name: /estimate from history/i }));

        expect(seen).toHaveLength(1);
        expect(seen[0].estimateSampleCount).toBe(3);
    });

    /*
     * The server REJECTS out of range rather than clamping, so a value the input allowed
     * through would come back as a 422 the user cannot act on.
     */
    it('clamps the sample size to the supported range', async () => {
        const seen: ScheduleValue[] = [];
        render(
            <RecurrenceBuilder
                value={{ ...defaultSchedule('2026-06-16'), estimateSampleCount: 3 }}
                onChange={(next) => seen.push(next)}
                estimateEligible
            />,
        );

        const input = screen.getByLabelText('Occurrences to average');
        await userEvent.clear(input);
        await userEvent.type(input, '99');

        expect(seen.at(-1)!.estimateSampleCount).toBeLessThanOrEqual(24);
    });
});
