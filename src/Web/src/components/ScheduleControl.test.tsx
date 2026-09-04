import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { ScheduleControl, type ScheduleSaveBody, type ScheduleView } from './ScheduleControl';

function renderControl(schedule: ScheduleView, enabledElsewhere = false) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const saved: ScheduleSaveBody[] = [];
    const view = render(
        <QueryClientProvider client={qc}>
            <ScheduleControl
                queryKey={['test-schedule']}
                load={() => Promise.resolve(schedule)}
                save={(body) => { saved.push(body); return Promise.resolve({ ...schedule, ...body }); }}
                label="Refresh prices automatically each day"
                note="uses the providers enabled above"
                enabledElsewhere={enabledElsewhere}
            />
        </QueryClientProvider>,
    );
    return { ...view, saved };
}

describe('ScheduleControl — timezone display honesty (C)', () => {
    it('flags a legacy enabled schedule with no timezone as server time', async () => {
        // A pre-#247 row: enabled but timezone NULL → fires at server-UTC. The
        // control must NOT claim the browser tz it isn't actually using.
        renderControl({ enabled: true, hourLocal: 19, minuteLocal: 0, timezone: null, nextRunAt: null });
        expect(await screen.findByText(/server time zone/)).toBeTruthy();
    });

    it('shows the stored timezone when one is set', async () => {
        renderControl({
            enabled: true, hourLocal: 19, minuteLocal: 0,
            timezone: 'America/New_York', nextRunAt: null,
        });
        expect(await screen.findByText(/America\/New_York/)).toBeTruthy();
    });
});

describe('ScheduleControl — job health', () => {
    // Every case here is DISABLED. That is deliberate: the control used to gate all
    // run information behind `enabled && nextRunAt`, so a job the scheduler switched
    // off rendered nothing at all — the one state worth showing was the one state
    // hidden. A health test written with `enabled: true` would pass against that bug.
    const off = {
        enabled: false, hourLocal: 19, minuteLocal: 0, timezone: 'UTC', nextRunAt: null,
    };

    it('says the scheduler switched it off, and how many failures it took', async () => {
        renderControl({
            ...off,
            consecutiveFailures: 5,
            lastError: 'the database rejected the operation',
            disabledReason: 'consecutive-failures',
        });
        expect(await screen.findByText(/Switched off automatically after 5 failed runs/))
            .toBeTruthy();
        expect(screen.getByText(/the database rejected the operation/)).toBeTruthy();
    });

    it('distinguishes a key-material disable from a failure disable', async () => {
        // The third actor. A two-valued auto/user model would report this as the
        // scheduler giving up, sending the reader to look for a failing job that
        // does not exist.
        renderControl({ ...off, consecutiveFailures: 0, disabledReason: 'key-material-missing' });
        expect(await screen.findByText(/stored passphrase could not be opened/)).toBeTruthy();
        expect(screen.queryByText(/Switched off automatically/)).toBeNull();
    });

    it('says nothing alarming about a job a person simply turned off', async () => {
        // Both assertions below are queryBy, so they pass against an empty DOM — and
        // this is the one case in the block with nothing of its own to await, because
        // a deliberately-disabled job with no failures is exactly the state where
        // ScheduleHealth renders NOTHING. Waiting on the checkbox would not fix it
        // either: it renders on the pending frame too, unchecked, indistinguishable
        // from the loaded state. So the wait anchors on a time that differs from the
        // 19:00 the control falls back to while the query is unresolved.
        //
        // Without that wait this test cannot fail. It was shipped that way, and a
        // mutation widening the failure branch to `|| !schedule.enabled` — which
        // renders "Switched off automatically after 0 failed runs" over a job the
        // operator turned off by hand — left it green.
        renderControl({
            ...off, hourLocal: 6, minuteLocal: 30,
            consecutiveFailures: 0, disabledReason: null,
        });
        expect(await screen.findByDisplayValue('06:30')).toBeTruthy();
        expect(screen.queryByText(/Switched off automatically/)).toBeNull();
        expect(screen.queryByText(/passphrase/)).toBeNull();
    });

    it('does not call a degraded run a failure', async () => {
        // last_error is populated on a run that COMPLETED but achieved less than it
        // should, with the counter at zero. Keying the failure badge off the message
        // instead of the count would paint every degraded run red.
        renderControl({
            enabled: true, hourLocal: 19, minuteLocal: 0, timezone: 'UTC', nextRunAt: null,
            consecutiveFailures: 0,
            lastError: 'one provider was unreachable',
            disabledReason: null,
        });
        expect(await screen.findByText(/completed with warnings/)).toBeTruthy();
        expect(screen.queryByText(/failed runs in a row/)).toBeNull();
    });

    it('warns about a failing job that is still enabled', async () => {
        renderControl({
            enabled: true, hourLocal: 19, minuteLocal: 0, timezone: 'UTC', nextRunAt: null,
            consecutiveFailures: 2, lastError: 'a network request failed or timed out',
        });
        expect(await screen.findByText(/2 failed runs in a row/)).toBeTruthy();
    });

    it('says ATTEMPTED, not succeeded, for the last run', async () => {
        // last_run_at is stamped when the scheduler claims the slot, before the
        // handler runs, so a job that has failed every night still has a fresh one.
        // The wording has to carry that or it states the opposite of the truth.
        renderControl({
            enabled: true, hourLocal: 19, minuteLocal: 0, timezone: 'UTC', nextRunAt: null,
            consecutiveFailures: 0, lastRunAt: new Date(Date.now() - 3600_000).toISOString(),
        });
        expect(await screen.findByText(/Last attempted/)).toBeTruthy();
    });
});

describe('ScheduleControl — a job switched on elsewhere', () => {
    /*
     * Reminder auto-post. Ticking "Auto-post / N days before" on a reminder IS the
     * opt-in, and the API turns the job on when a reminder first asks for it. A second
     * checkbox here would be a switch the user has to go and find before their first
     * tick did anything — the original defect, one level up.
     */
    it('offers no on/off checkbox, only the time', async () => {
        renderControl(
            {
                enabled: true, hourLocal: 5, minuteLocal: 0, timezone: 'UTC',
                nextRunAt: '2026-09-04T05:00:00Z',
            },
            true);

        // Same reasoning as below: wait for something only the LOADED state renders.
        expect(await screen.findByText(/Next run:/)).toBeTruthy();
        expect(screen.queryByRole('checkbox')).toBeNull();
    });

    /*
     * There is no way back because there is nowhere to come back FROM. This job has no
     * disabled state: it runs because a reminder asked to be auto-posted and stops
     * because none does.
     *
     * A "Turn back on" control shipped here briefly, and appeared for jobs nobody had
     * ever turned on — GET synthesizes `enabled: false` when no row exists, so
     * "configured and off" and "never configured" were indistinguishable. The button was
     * built for a state that should not exist; deleting the state deleted the bug.
     */
    it('offers no way to switch it on or off, even when the stored row is off', async () => {
        renderControl(
            {
                enabled: false, hourLocal: 5, minuteLocal: 0, timezone: 'UTC',
                nextRunAt: null, consecutiveFailures: 5, lastError: 'boom',
                disabledReason: 'consecutive-failures',
            },
            true);

        // findByDisplayValue, NOT findByLabelText: the input renders on the pending frame
        // too, with the 19:00 fallback, so waiting on the label proves nothing about the
        // loaded state and both assertions below would be vacuous. Verified by mutation.
        expect(await screen.findByDisplayValue('05:00')).toBeTruthy();
        expect(screen.queryByRole('checkbox')).toBeNull();
        expect(screen.queryByRole('button', { name: /turn back on/i })).toBeNull();
    });

    /*
     * And setting a time must never write `enabled: false`. That is the bug that made
     * this worse than cosmetic: adjusting the time before any auto-post reminder existed
     * created a DISABLED row, and since a reminder only creates the schedule when absent
     * — never flipping an existing one — auto-post could then never turn itself on.
     */
    it('saves a time change as enabled, even when the stored row says off', async () => {
        const { saved } = renderControl(
            { enabled: false, hourLocal: 5, minuteLocal: 0, timezone: 'UTC', nextRunAt: null },
            true);

        const input = await screen.findByDisplayValue('05:00');
        fireEvent.change(input, { target: { value: '07:30' } });

        await waitFor(() => expect(saved).toHaveLength(1));
        expect(saved[0].enabled).toBe(true);
        expect(saved[0].hourLocal).toBe(7);
        expect(saved[0].minuteLocal).toBe(30);
    });

});
