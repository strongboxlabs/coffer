import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { ScheduleControl, type ScheduleView } from './ScheduleControl';

function renderControl(schedule: ScheduleView) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <ScheduleControl
                queryKey={['test-schedule']}
                load={() => Promise.resolve(schedule)}
                save={(body) => Promise.resolve({ ...schedule, ...body })}
                label="Refresh prices automatically each day"
                note="uses the providers enabled above"
            />
        </QueryClientProvider>,
    );
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
