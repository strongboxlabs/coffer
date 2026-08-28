import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { NotificationsPanel } from './NotificationsPanel';
import * as apiModule from '@/lib/api';

// NotificationsPanel — the absence-detection warning is the point of this screen.
// Someone who wires a chat webhook, sees a test message arrive and concludes they
// are covered has kept the exact failure mode that ran for 68 hours unnoticed, so
// these tests pin that the panel says so — twice, at the point of choosing and as a
// standing banner.

function renderPanel() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            <NotificationsPanel />
        </QueryClientProvider>,
    );
}

const PROVIDERS = [
    { subscriberKey: 'healthchecks', displayName: 'Healthchecks', detectsAbsence: true, monitors: ['backup'] },
    { subscriberKey: 'webhook', displayName: 'Webhook', detectsAbsence: false, monitors: [] },
];

describe('NotificationsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchNotificationProviders').mockResolvedValue(PROVIDERS);
        vi.spyOn(apiModule, 'fetchSystemEvents').mockResolvedValue([]);
    });

    it('warns when nothing configured can detect silence', async () => {
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [
                {
                    id: 'a', subscriberKey: 'webhook', displayName: 'Discord',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                                monitors: null,
                    lastSuccessAt: null, lastFailureAt: null, lastError: null,
                    consecutiveFailures: 0,
                },
            ],
            monitorCoverage: { backup: false },
        });

        renderPanel();

        // A configured target exists and the install still cannot notice its own
        // silence — saying nothing here would leave someone falsely reassured. And it
        // NAMES the unwatched job: one healthchecks URL is one check, so "absence
        // detection is covered" was never a statement the old boolean could honestly
        // make about any particular thing.
        expect(await screen.findByText(/nothing is watching for: backup/i)).toBeTruthy();
    });

    it('stays quiet once a heartbeat target exists', async () => {
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [
                {
                    id: 'b', subscriberKey: 'healthchecks', displayName: 'HC',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                                monitors: null,
                    lastSuccessAt: '2026-08-19T00:00:00Z', lastFailureAt: null,
                    lastError: null, consecutiveFailures: 0,
                },
            ],
            monitorCoverage: { backup: true },
        });

        renderPanel();

        await screen.findByText(/HC/);
        // Guards the opposite failure: a warning that never goes away is noise, and
        // noise is what gets a channel muted.
        expect(screen.queryByText(/nothing is watching for/i)).toBeNull();
    });

    it('keeps the uncovered warning when a REFETCH fails, rather than downgrading it', async () => {
        // The banner is gated on "no data", not on isError. React Query sets isError on
        // a failed background refetch while `data` still holds the last good response —
        // and a known "nothing is watching backups" must not be replaced by "unknown
        // right now", which reads as softer than the truth. No data is unknown; stale
        // data is still data.
        const uncovered = {
            subscribers: [
                {
                    id: 'a', subscriberKey: 'webhook', displayName: 'Discord',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                    monitors: null,
                    lastSuccessAt: null, lastFailureAt: null, lastError: null,
                    consecutiveFailures: 0,
                },
            ],
            monitorCoverage: { backup: false },
        };
        const fetchSubscribers = vi
            .spyOn(apiModule, 'fetchNotificationSubscribers')
            .mockResolvedValueOnce(uncovered)
            .mockRejectedValue(new Error('network'));

        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false } },
        });
        render(
            <QueryClientProvider client={queryClient}>
                <NotificationsPanel />
            </QueryClientProvider>,
        );

        expect(await screen.findByText(/nothing is watching for: backup/i)).toBeTruthy();

        await queryClient.refetchQueries();
        await waitFor(() => expect(fetchSubscribers.mock.calls.length).toBeGreaterThan(1));

        // Still the real warning, not the "could not load" fallback.
        expect(screen.queryByText(/nothing is watching for: backup/i)).toBeTruthy();
        expect(screen.queryByText(/could not load delivery targets/i)).toBeNull();
    });

    it('says the coverage is unknown when there is no data at all', async () => {
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockRejectedValue(
            new Error('network'),
        );

        renderPanel();

        // The other half of the same rule: with nothing loaded, an empty `uncovered`
        // means "we do not know", and silently rendering no banner would have been the
        // strongest possible claim made on no evidence.
        expect(await screen.findByText(/could not load delivery targets/i)).toBeTruthy();
        expect(screen.queryByText(/nothing is watching for/i)).toBeNull();
    });

    it('offers no severity floor for a provider whose routing never reads one', async () => {
        // Wants() matches a heartbeat target on monitor name + signal and returns before
        // the severity floor is consulted, so "Send at" changed nothing on healthchecks.
        // A control that does nothing is the looks-configured-does-nothing shape this
        // whole screen exists to warn about, so it is not shown. Topics already followed
        // this rule; severity did not.
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { backup: false },
        });

        renderPanel();
        const user = userEvent.setup();

        // Wait for the provider list to land: findByLabelText resolves as soon as the
        // label exists, which is before the query populates the options.
        await screen.findByRole('option', { name: /Healthchecks/i });

        // A message provider: the floor is real and must stay.
        await user.selectOptions(screen.getByLabelText(/provider/i), 'webhook');
        expect(screen.getByText(/send at/i)).toBeTruthy();

        // A dead-man's switch: the floor is meaningless and must go, replaced by what
        // the provider ACTUALLY does with a failure.
        await user.selectOptions(screen.getByLabelText(/provider/i), 'healthchecks');
        expect(screen.queryByText(/send at/i)).toBeNull();
        expect(screen.getByText(/posts to/i)).toBeTruthy();
        expect(screen.getByText('/fail')).toBeTruthy();
    });

    it('stores the default floor for a heartbeat target, not a stale hidden choice', async () => {
        // Switching provider hides the field but not the state behind it. Without this,
        // picking "Only problems needing attention" on a webhook and then switching to
        // healthchecks stored critical on a row whose screen shows no severity at all
        // and whose router never reads one — a row disagreeing with the page that made it.
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { backup: false },
        });
        const create = vi
            .spyOn(apiModule, 'createNotificationSubscriber')
            .mockResolvedValue(undefined as never);

        renderPanel();
        const user = userEvent.setup();
        await screen.findByRole('option', { name: /Healthchecks/i });

        await user.selectOptions(screen.getByLabelText(/provider/i), 'webhook');
        await user.selectOptions(screen.getByLabelText(/send at/i), 'critical');

        await user.selectOptions(screen.getByLabelText(/provider/i), 'healthchecks');
        await user.selectOptions(screen.getByLabelText(/watches/i), 'backup');
        await user.type(screen.getByLabelText(/URL/i), 'https://hc.invalid/abc');
        await user.click(screen.getByRole('button', { name: /add target/i }));

        await waitFor(() => expect(create).toHaveBeenCalled());
        expect(create.mock.calls[0][0]).toMatchObject({
            subscriberKey: 'healthchecks',
            monitors: 'backup',
            minSeverity: 'warning',
        });
    });

    it('describes a heartbeat target by what it watches, not by severity', async () => {
        // The configured-targets list said "at warning and above · all topics" for every
        // row. On a heartbeat target both halves are untrue: it is routed by neither.
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [
                {
                    id: 'h', subscriberKey: 'healthchecks', displayName: 'HC',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                    monitors: 'backup',
                    lastSuccessAt: null, lastFailureAt: null, lastError: null,
                    consecutiveFailures: 0,
                },
            ],
            monitorCoverage: { backup: true },
        });

        renderPanel();

        expect(await screen.findByText(/watches backup/i)).toBeTruthy();
        expect(screen.queryByText(/at warning and above/i)).toBeNull();
    });

    it('surfaces a target that has stopped delivering', async () => {
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [
                {
                    id: 'c', subscriberKey: 'webhook', displayName: 'Discord',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                                monitors: null,
                    lastSuccessAt: null, lastFailureAt: '2026-08-19T00:00:00Z',
                    lastError: '401 Unauthorized', consecutiveFailures: 4,
                },
            ],
            monitorCoverage: { backup: false },
        });

        renderPanel();

        // A target whose token was rotated must not look identical to a working one.
        expect(await screen.findByText(/4 consecutive failures/i)).toBeTruthy();
        expect(screen.getByText(/401 Unauthorized/)).toBeTruthy();
    });

    it('warns at the moment a message-only provider is chosen', async () => {
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { backup: false },
        });

        renderPanel();
        const user = userEvent.setup();

        const select = await screen.findByLabelText(/provider/i);
        // Wait for the options themselves: the provider list is a separate query, so
        // the label resolves before the choices exist.
        await screen.findByRole('option', { name: /Webhook/ });
        await user.selectOptions(select, 'webhook');

        // Said where the decision is made, not only in a banner further up.
        await waitFor(() =>
            expect(
                screen.getByText(/cannot tell you when something fails to happen/i),
            ).toBeTruthy());

        await user.selectOptions(select, 'healthchecks');
        expect(
            screen.queryByText(/cannot tell you when something fails to happen/i),
        ).toBeNull();
    });

    it('says the events list failed rather than showing it as quiet', async () => {
        // The bug this replaces: on error `events.data` is undefined, so a
        // `data?.length === 0` test was false and the panel rendered an EMPTY LIST.
        // A failed query looked exactly like a quiet install — the one confusion this
        // whole subsystem exists to remove, reproduced in the surface that reports it.
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { backup: true },
        });
        vi.spyOn(apiModule, 'fetchSystemEvents').mockRejectedValue(new Error('boom'));

        renderPanel();

        expect(await screen.findByText(/could not load recent events/i)).toBeTruthy();
        // And explicitly NOT the quiet wording, which would be a lie.
        expect(screen.queryByText(/nothing recorded yet/i)).toBeNull();
    });

    it('does not claim coverage when the targets could not be loaded', async () => {
        // Worse than the events case: a failed subscribers query left the coverage map
        // empty, so `uncovered` was empty and the warning banner simply vanished —
        // the strongest possible claim made on the strength of no data at all.
        vi.spyOn(apiModule, 'fetchNotificationSubscribers').mockRejectedValue(
            new Error('boom'),
        );
        vi.spyOn(apiModule, 'fetchSystemEvents').mockResolvedValue([]);

        renderPanel();

        expect(await screen.findByText(/could not load delivery targets/i)).toBeTruthy();
        expect(screen.queryByText(/nothing is watching for/i)).toBeNull();
    });
});
