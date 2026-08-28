import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { NotificationsPanel } from './NotificationsPanel';
import * as apiModule from '@/lib/api';

// Per-ledger notifications (ADR-0096). The two states worth pinning are the ones a
// user can land in and be wrong about: no targets configured, which is silence and
// must say so, and choosing a target that cannot detect absence. There is no longer
// a mode to get wrong — migration 214 retired 'inherit'.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

const PROVIDERS = [
    {
        subscriberKey: 'healthchecks',
        displayName: 'Healthchecks',
        detectsAbsence: true,
        // The LEDGER monitors. Never the deployment's backup, which no
        // ledger runs and which a ledger switch could therefore never trip.
        monitors: ['quote-refresh', 'snapshot'],
    },
    { subscriberKey: 'webhook', displayName: 'Webhook', detectsAbsence: false, monitors: [] },
];

function renderPanel() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            <NotificationsPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

describe('Ledger NotificationsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchLedgerNotificationProviders').mockResolvedValue(PROVIDERS);
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
    });

    it('shows the target list unconditionally now that a ledger owns its targets', async () => {
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
        });

        renderPanel();

        // Replaces 'hides the target list while inheriting'. There is no mode to hide it
        // behind: a ledger's events go to a ledger's targets, so the configuration is
        // always the relevant configuration.
        expect(await screen.findByText(/my targets/i)).toBeTruthy();
        expect(screen.queryByText(/use this installation's targets/i)).toBeNull();
    });

    it('says plainly when a ledger reports nothing anywhere', async () => {
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
        });

        renderPanel();

        // The state a user lands in by choosing "own" and not finishing. Silence is a
        // legitimate choice; silence someone did not realise they chose is not.
        expect(
            await screen.findByText(/currently reports nothing anywhere/i),
        ).toBeTruthy();
    });

    it('offers a switch bound to one of this ledger\'s own jobs', async () => {
        // Reverses the refusal shipped earlier in this branch. That refusal was right
        // about the code — every monitor was a deployment job, so a ledger switch could
        // never fire — and wrong about the design: quote-refresh and snapshot ARE
        // per-ledger scheduled jobs, and they are exactly the things that go quiet
        // without anyone noticing. The scheduler now emits a signal per run for each.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { 'quote-refresh': false },
        });
        const create = vi
            .spyOn(apiModule, 'createLedgerNotificationSubscriber')
            .mockResolvedValue(undefined as never);

        renderPanel();
        const user = userEvent.setup();
        await screen.findByRole('option', { name: /Healthchecks/i });

        await user.selectOptions(screen.getByLabelText(/provider/i), 'healthchecks');

        // This dropdown was already in the component and was unreachable code:
        // ListProviders filtered heartbeat providers out and then hardcoded
        // detectsAbsence=false on everything left, so it never rendered — and this
        // file's own fixture mocked a provider the real API could not return.
        await user.selectOptions(screen.getByLabelText(/watches/i), 'quote-refresh');
        await user.type(screen.getByLabelText(/^URL/i), 'https://hc.invalid/abc');
        await user.click(screen.getByRole('button', { name: /add target/i }));

        await waitFor(() => expect(create).toHaveBeenCalled());
        expect(create.mock.calls[0][1]).toMatchObject({
            subscriberKey: 'healthchecks',
            monitors: 'quote-refresh',
        });
    });

    it('names the jobs nothing is watching', async () => {
        // Per job, because one healthchecks URL is one check: a switch on the quote
        // refresh says nothing whatever about snapshots.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { 'quote-refresh': true, snapshot: false },
        });

        renderPanel();

        const warning = await screen.findByText(/nothing is watching for: snapshot/i);
        expect(warning).toBeTruthy();
        // ...and it does not name the covered one, or the warning is noise.
        expect(warning.textContent).not.toMatch(/quote-refresh/);
    });

    it('says nothing about a job this ledger does not run', async () => {
        // The cry-wolf boundary. Coverage is keyed on the ledger's ENABLED jobs, so a
        // ledger with snapshots switched off is never warned about snapshots — warning
        // about a job that is not supposed to run is how a channel earns being muted.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { 'quote-refresh': true },
        });

        renderPanel();

        await screen.findByText(/my targets/i);
        expect(screen.queryByText(/nothing is watching for/i)).toBeNull();
    });

    it('offers no severity floor for a switch, whose routing never reads one', async () => {
        // The same rule the deployment panel already learned: Wants() matches a heartbeat
        // target on monitor name and signal and returns before the severity floor is
        // consulted, so offering the choice would be offering a control that does nothing.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
        });

        renderPanel();
        const user = userEvent.setup();
        await screen.findByRole('option', { name: /Healthchecks/i });

        await user.selectOptions(screen.getByLabelText(/provider/i), 'webhook');
        expect(screen.getByText(/send at/i)).toBeTruthy();

        await user.selectOptions(screen.getByLabelText(/provider/i), 'healthchecks');
        expect(screen.queryByText(/send at/i)).toBeNull();
        expect(screen.getByText('/fail')).toBeTruthy();
    });

    it('lists recent problems, and says routine successes are absent by design', async () => {
        // ADR-0096 D5's promised in-app surface. The copy matters as much as the list: an
        // empty list must not read as "nothing has happened" when it means "nothing has
        // gone wrong".
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e1',
                occurredAt: '2026-08-26T03:00:00Z',
                severity: 'warning',
                topic: 'quotes',
                eventKey: 'quote-refresh.failed',
                summary: 'Scheduled quote-refresh did not complete: yahoo timed out',
                resolvedAt: null,
            },
        ]);

        renderPanel();

        expect(await screen.findByText(/yahoo timed out/i)).toBeTruthy();

        // WHEN, not just what. Without a time, a six-day-old warning that has since been
        // resolved is indistinguishable from one raised a minute ago — which is exactly
        // what the first real row this panel ever showed turned out to be.
        const when = document.querySelector('time[datetime="2026-08-26T03:00:00Z"]');
        expect(when).not.toBeNull();
        expect(when!.textContent?.trim()).not.toBe('');
    });

    it('shows a resolved problem as resolved rather than hiding it', async () => {
        // The failure that prompted this: a six-day-old drift warning that had already
        // been fixed, indistinguishable from one raised a minute ago. Hiding it would
        // trade one wrong impression for another — it did happen — so it is marked.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e2',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: '2026-08-26T09:00:00Z',
            },
        ]);

        renderPanel();

        expect(await screen.findByText(/3 of 175 disagree/i)).toBeTruthy();
        expect(screen.getByText(/^resolved$/i)).toBeTruthy();
    });

    it('distinguishes a failed events fetch from a quiet ledger', async () => {
        // Same rule the deployment panel learned: saying nothing when the request failed
        // is the strongest possible claim made on no data.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockRejectedValue(new Error('network'));

        renderPanel();

        expect(await screen.findByText(/could not load recent problems/i)).toBeTruthy();
        expect(screen.queryByText(/no warnings or problems recorded/i)).toBeNull();
    });

    it('never implies the list is coverage', async () => {
        // ADR-0096 rejected "in-app notifications only" because it produced 68 hours of
        // silence. A page you have to open cannot tell you the app stopped running, and
        // the panel has to say so where the list is, not only where the target is.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
        });

        renderPanel();

        expect(
            await screen.findByText(/cannot tell you the app stopped running/i),
        ).toBeTruthy();
    });

    it('warns that a message target cannot detect silence', async () => {
        // The surviving half of 'switches mode and warns about a message-only target'.
        // The mode-switching half tested a control that no longer exists; the warning is
        // still the point — a ledger owner picking their own target can make exactly the
        // same mistake an admin can.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [
                {
                    id: 'a', subscriberKey: 'webhook', displayName: 'Discord',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                    monitors: null,
                    lastSuccessAt: null, lastFailureAt: null, lastError: null,
                    consecutiveFailures: 0,
                },
            ],
            monitorCoverage: {},
        });

        renderPanel();
        const user = userEvent.setup();

        const select = await screen.findByLabelText(/provider/i);
        await screen.findByRole('option', { name: /Webhook/ });
        await user.selectOptions(select, 'webhook');
        expect(
            screen.getByText(/cannot tell you when something fails to happen/i),
        ).toBeTruthy();
    });
});
