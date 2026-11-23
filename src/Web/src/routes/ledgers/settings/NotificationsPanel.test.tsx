import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

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

/**
 * The same panel inside a router.
 *
 * An unresolved drift event renders a Link, which needs router context; the plain
 * harness above has none. Kept as a second function rather than converting the
 * first, so the fifteen existing cases keep exercising the panel with no router —
 * which is what most of them are actually about.
 */
function renderPanelRouted(
    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }),
) {
    const root = createRootRoute();
    const here = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <NotificationsPanel ledgerId={LEDGER_ID} />,
    });
    const settings = createRoute({
        getParentRoute: () => root,
        path: '/ledgers/$ledgerId/settings',
        component: () => <main>settings</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([here, settings]),
        history: createMemoryHistory({ initialEntries: ['/'] }),
        context: { queryClient },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
}

describe('Ledger NotificationsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchLedgerNotificationProviders').mockResolvedValue(PROVIDERS);
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        // The owner's view by default: where a ledger reports is theirs (FU-095).
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
            { id: LEDGER_ID, name: 'Personal', role: 'owner', currencyCode: 'USD' },
        ]);
    });

    it("shows an editor where the ledger reports, and not the owner's add or remove", async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
            { id: LEDGER_ID, name: 'Personal', role: 'editor', currencyCode: 'USD' },
        ]);
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [
                {
                    id: 'msg', subscriberKey: 'webhook', displayName: 'Discord',
                    isEnabled: true, minSeverity: 'warning', topics: null, monitors: null,
                    lastSuccessAt: null, lastFailureAt: null, lastError: null,
                    consecutiveFailures: 0,
                },
            ],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });

        renderPanel();

        // FU-095: an editor could point the ledger's events at an address of their own.
        const row = (await screen.findByText('Discord')).closest('div')!.parentElement!;
        expect(within(row).queryByRole('button', { name: /remove/i })).toBeNull();
        expect(screen.getByText(/only an owner of this ledger can add or remove/i)).toBeTruthy();
        expect(screen.queryByRole('button', { name: /add target/i })).toBeNull();
    });

    it('shows the target list unconditionally now that a ledger owns its targets', async () => {
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
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
            monitorsWatchingNothing: [],
        });

        renderPanel();

        // The state a user lands in by choosing "own" and not finishing. Silence is a
        // legitimate choice; silence someone did not realise they chose is not.
        expect(
            await screen.findByText(/currently reports nothing anywhere/i),
        ).toBeTruthy();
    });

    it('offers a switch bound to one of this ledger\'s own jobs', async () => {
        // Quote-refresh and snapshot are per-ledger scheduled jobs, and they are
        // exactly the things that go quiet without anyone noticing. The scheduler
        // emits a signal per run for each, so a ledger switch bound to one can fire.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { 'quote-refresh': false },
            monitorsWatchingNothing: [],
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

    it('says the quote refresh switch also watches exchange rates and benchmarks, which ride that job', async () => {
        // ADR-0114 D11: rates and benchmarks are fetched by the quote refresh, so there is
        // no job of their own to pick. Without the reading, "quote-refresh" reads as
        // prices only and an owner looking for a currency watch finds none.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [{
                id: 's1', subscriberKey: 'healthchecks', displayName: 'HC quotes',
                isEnabled: true, minSeverity: 'warning', topics: null, monitors: 'quote-refresh',
                lastSuccessAt: null, lastFailureAt: null, lastError: null,
                consecutiveFailures: 0,
            }],
            monitorCoverage: { 'quote-refresh': true, snapshot: false },
            monitorsWatchingNothing: [],
        });

        renderPanel();
        const user = userEvent.setup();
        await screen.findByRole('option', { name: /Healthchecks/i });
        await user.selectOptions(screen.getByLabelText(/provider/i), 'healthchecks');

        const watches = screen.getByLabelText(/watches/i);
        expect(within(watches).getByRole('option', { name: 'quote-refresh — prices, exchange rates, benchmarks' }))
            .toHaveValue('quote-refresh');
        // A job whose name says what it covers is shown as it is.
        expect(within(watches).getByRole('option', { name: 'snapshot' })).toHaveValue('snapshot');
        // The bound target's line reads the same way.
        expect(screen.getByText(/watches quote-refresh — prices, exchange rates, benchmarks/)).toBeInTheDocument();
    });

    it('gives focus to the URL field when an added target clears it and leaves Add target disabled', async () => {
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        const create = vi
            .spyOn(apiModule, 'createLedgerNotificationSubscriber')
            .mockResolvedValue(undefined as never);

        renderPanel();
        const user = userEvent.setup();
        await screen.findByRole('option', { name: /Webhook/i });
        await user.selectOptions(screen.getByLabelText(/provider/i), 'webhook');
        const url = screen.getByLabelText(/^URL/i);
        await user.type(url, 'https://hook.invalid/abc');
        const add = screen.getByRole('button', { name: /add target/i });
        await user.click(add);

        await waitFor(() => expect(create).toHaveBeenCalled());
        await waitFor(() => expect(add).toBeDisabled());
        expect(url).toHaveValue('');
        expect(url).toHaveFocus();
    });

    // ADR-0023 §L: while the add is in flight its fields are pending. Its success
    // clears the name and URL, so anything typed meanwhile would be dropped, and a
    // severity picked meanwhile would never be sent.
    it('refuses typing and picks in the add form while the add is in flight', async () => {
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'createLedgerNotificationSubscriber')
            .mockImplementation(() => new Promise(() => {}));

        renderPanel();
        const user = userEvent.setup();
        await screen.findByRole('option', { name: /Webhook/i });
        await user.selectOptions(screen.getByLabelText(/provider/i), 'webhook');
        const name = screen.getByLabelText(/^Name/i);
        const url = screen.getByLabelText(/^URL/i);
        const severity = screen.getByLabelText(/send at/i);
        await user.type(name, 'Target A');
        await user.type(url, 'https://hook.invalid/abc');
        await user.click(screen.getByRole('button', { name: /add target/i }));
        await screen.findByRole('button', { name: /adding/i });

        await user.type(name, ' more');
        await user.type(url, 'more');
        await user.selectOptions(severity, 'critical');
        await user.selectOptions(screen.getByLabelText(/provider/i), '');

        expect(name).toHaveValue('Target A');
        expect(url).toHaveValue('https://hook.invalid/abc');
        expect(severity).not.toHaveValue('critical');
        expect(screen.getByLabelText(/provider/i)).toHaveValue('webhook');
        for (const field of [name, url, severity]) expect(field).toHaveAttribute('aria-disabled', 'true');
    });

    it('names the jobs nothing is watching', async () => {
        // Per job, because one healthchecks URL is one check: a switch on the quote
        // refresh says nothing whatever about snapshots.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { 'quote-refresh': true, snapshot: false },
            monitorsWatchingNothing: [],
        });

        renderPanel();

        const warning = await screen.findByText(/nothing is watching for: snapshot/i);
        expect(warning).toBeTruthy();
        // ...and it does not name the covered one, or the warning is noise.
        expect(warning.textContent).not.toMatch(/quote-refresh/);
    });

    it('names a switch that is watching a job this ledger does not run', async () => {
        // The bank-feed case. A healthchecks URL bound to feed-sync on a ledger with the
        // feed-sync schedule off reads "Never" forever: the job cannot ping, and the
        // coverage map above cannot mention it because coverage is keyed on the jobs that
        // DO run. Note the fixture — the monitor is deliberately absent from
        // monitorCoverage, which is exactly how the API reports it.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: ['feed-sync'],
        });

        renderPanel();

        const warning = await screen.findByText(
            /watching a job that is not scheduled: feed-sync/i,
        );
        expect(warning.textContent).toMatch(/never be pinged/i);
    });

    it('does not invent that complaint when every bound switch has its job on', async () => {
        // Keeps the test above from passing on a panel that renders the warning
        // unconditionally: a check that cannot fail.
        //
        // The anchor is load-bearing. Waiting on the 'Notifications' heading would prove
        // nothing: it renders on the pending frame, so the absence assertion would pass
        // before the query resolved and a panel that always showed the warning would
        // still go green. Anchoring on the OTHER warning fixes that
        // — it can only appear once settings.data exists — and it doubles as proof the
        // two messages stay distinct rather than one matching the other's text.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { 'feed-sync': false },
            monitorsWatchingNothing: [],
        });

        renderPanel();

        await screen.findByText(/nothing is watching for: feed-sync/i);
        expect(screen.queryByText(/is not scheduled/i)).toBeNull();
    });

    it('says nothing about a job this ledger does not run', async () => {
        // The cry-wolf boundary. Coverage is keyed on the ledger's ENABLED jobs, so a
        // ledger with snapshots switched off is never warned about snapshots — warning
        // about a job that is not supposed to run is how a channel earns being muted.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: { 'quote-refresh': true },
            monitorsWatchingNothing: [],
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
            monitorsWatchingNothing: [],
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

    it('describes a switch by what it watches, not by a floor it never reads', async () => {
        // A saved row saying "at warning and above" under a switch whose create form
        // says there is no severity to choose contradicts itself — and the form is the
        // half telling the truth. Wants() branches on capability BEFORE any floor check
        // and the heartbeat arm returns on monitor name and signal alone, so MinSeverity
        // is never read for this row.
        //
        // It is not a harmless label. A successful run publishes at info, strictly BELOW
        // a warning floor: if the line described real routing, every success ping would be
        // dropped and the check would go red from inactivity. The stored "warning" is just
        // the create form's hidden default.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [
                {
                    id: 'hb', subscriberKey: 'healthchecks', displayName: 'Bank feed check',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                    monitors: 'feed-sync',
                    lastSuccessAt: null, lastFailureAt: null, lastError: null,
                    consecutiveFailures: 0,
                },
            ],
            monitorCoverage: { 'feed-sync': true },
            monitorsWatchingNothing: [],
        });

        renderPanel();

        expect(
            await screen.findByText(/watches feed-sync . pings on success, \/fail on failure/i),
        ).toBeTruthy();
        expect(screen.queryByText(/at warning and above/i)).toBeNull();
    });

    it('still states the floor for a target that genuinely has one', async () => {
        // The other half, and the reason the fix is a branch rather than a deletion. A
        // message target IS routed by severity and topic, so removing the line outright
        // would replace one false statement with a missing true one.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [
                {
                    id: 'msg', subscriberKey: 'webhook', displayName: 'Discord',
                    isEnabled: true, minSeverity: 'warning', topics: null,
                    monitors: null,
                    lastSuccessAt: null, lastFailureAt: null, lastError: null,
                    consecutiveFailures: 0,
                },
            ],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });

        renderPanel();

        expect(await screen.findByText(/at warning and above/i)).toBeTruthy();
        expect(screen.queryByText(/pings on success/i)).toBeNull();
    });

    it('stores the default severity for a switch instead of a hidden leftover', async () => {
        // The write half of the same bug. The select is hidden for a heartbeat provider,
        // but the state behind it kept whatever was last chosen while a message provider
        // was selected — so the row could store a floor the owner never saw and the router
        // never reads. That is how a row starts disagreeing with the screen that made it.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        const create = vi
            .spyOn(apiModule, 'createLedgerNotificationSubscriber')
            .mockResolvedValue(undefined as never);

        renderPanel();
        const user = userEvent.setup();
        await screen.findByRole('option', { name: /Healthchecks/i });

        // Pick a message provider first and move the floor off its default, so the
        // assertion below proves the value was REPLACED rather than never set.
        await user.selectOptions(screen.getByLabelText(/provider/i), 'webhook');
        await user.selectOptions(screen.getByLabelText(/send at/i), 'critical');

        await user.selectOptions(screen.getByLabelText(/provider/i), 'healthchecks');
        await user.selectOptions(screen.getByLabelText(/watches/i), 'quote-refresh');
        await user.type(screen.getByLabelText(/^URL/i), 'https://hc.invalid/xyz');
        await user.click(screen.getByRole('button', { name: /add target/i }));

        await waitFor(() => expect(create).toHaveBeenCalled());
        expect(create.mock.calls[0][1]).toMatchObject({
            subscriberKey: 'healthchecks',
            monitors: 'quote-refresh',
            minSeverity: 'warning',
        });
    });

    it('lists recent problems, and says routine successes are absent by design', async () => {
        // ADR-0096 D5's promised in-app surface. The copy matters as much as the list: an
        // empty list must not read as "nothing has happened" when it means "nothing has
        // gone wrong".
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
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

        // WHEN, not just what. Without a time, a days-old warning that has since been
        // resolved is indistinguishable from one raised a minute ago.
        const when = document.querySelector('time[datetime="2026-08-26T03:00:00Z"]');
        expect(when).not.toBeNull();
        expect(when!.textContent?.trim()).not.toBe('');
    });

    it('shows a resolved problem as resolved rather than hiding it', async () => {
        // The failure that prompted this: a days-old drift warning that had already
        // been fixed, indistinguishable from one raised a minute ago. Hiding it would
        // trade one wrong impression for another — it did happen — so it is marked.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
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

    it('hands an unresolved drift finding its own fix', async () => {
        // The gap this closes: the scheduled monitor finds the drift and says so, and
        // the reader then has to go to another tab and re-run a check that already ran
        // to see what drifted or repair it. The finding should carry the fix.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e3',
                occurredAt: '2026-08-26T09:00:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: null,
            },
        ]);

        renderPanelRouted();

        const action = await screen.findByRole('link', { name: /check and repair/i });
        // It must land on General with the arrival flag, or it is a link to a panel
        // that shows nothing.
        expect(action.getAttribute('href')).toContain('check=true');
    });

    // An editor writes, so an editor repairs, as an owner does: the fix follows who can
    // edit, not who owns the ledger.
    it('hands an editor the drift finding\'s fix', async () => {
        const editorLedgers = [{ id: LEDGER_ID, name: 'Personal', role: 'editor', currencyCode: 'USD' }];
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(editorLedgers);
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e3',
                occurredAt: '2026-08-26T09:00:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: null,
            },
        ]);

        // The role is known on the first frame, as in the viewer's case below.
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        queryClient.setQueryData(['ledgers'], editorLedgers);

        renderPanelRouted(queryClient);

        expect(await screen.findByText(/3 of 175 disagree/i)).toBeTruthy();
        const action = screen.getByRole('link', { name: /check and repair/i });
        expect(action.getAttribute('href')).toContain('check=true');
    });

    // General offers a viewer no Repair, since the server refuses one, so the finding
    // does not offer one either: a viewer's link reads "Details" and leads to General
    // without the check, as the Overview's Problems does.
    it('offers a viewer no repair for an unresolved drift finding', async () => {
        const viewerLedgers = [{ id: LEDGER_ID, name: 'Personal', role: 'viewer', currencyCode: 'USD' }];
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(viewerLedgers);
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e3',
                occurredAt: '2026-08-26T09:00:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: null,
            },
        ]);
        // The ledger list is in the cache before the first frame, so the reader's role
        // is known on every frame and the absence below is not that of a frame before it.
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        queryClient.setQueryData(['ledgers'], viewerLedgers);

        renderPanelRouted(queryClient);

        // The finding's row draws its link in the same frame as its summary.
        expect(await screen.findByText(/3 of 175 disagree/i)).toBeTruthy();
        expect(screen.queryByRole('link', { name: /check and repair/i })).toBeNull();
        const details = screen.getByRole('link', { name: /^details$/i });
        expect(details.getAttribute('href')).toBe(`/ledgers/${LEDGER_ID}/settings`);
    });

    it('does not offer to re-check drift that is already resolved', async () => {
        // The negative half. Without it the link could render unconditionally and the
        // test above would still pass — and the cost is not cosmetic: the check walks
        // every position, so offering it for a problem already reported healthy is an
        // invitation to do that work for nothing.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e4',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: '2026-08-26T09:00:00Z',
            },
        ]);

        renderPanelRouted();

        expect(await screen.findByText(/3 of 175 disagree/i)).toBeTruthy();
        expect(screen.queryByRole('link', { name: /check and repair/i })).toBeNull();
    });

    it('distinguishes a failed events fetch from a quiet ledger', async () => {
        // Same rule the deployment panel learned: saying nothing when the request failed
        // is the strongest possible claim made on no data.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockRejectedValue(new Error('network'));

        renderPanel();

        expect(await screen.findByText(/could not load recent problems/i)).toBeTruthy();
        expect(screen.queryByText(/no warnings or problems recorded/i)).toBeNull();
    });

    it('never implies the list is coverage', async () => {
        // ADR-0096 rejected "in-app notifications only" because it produced days of
        // silence. A page you have to open cannot tell you the app stopped running, and
        // the panel has to say so where the list is, not only where the target is.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });

        renderPanel();

        expect(
            await screen.findByText(/cannot tell you the app stopped running/i),
        ).toBeTruthy();
    });

    it('warns that a message target cannot detect silence', async () => {
        // The warning is the point: a ledger owner picking their own target can make
        // exactly the same mistake an admin can.
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
            monitorsWatchingNothing: [],
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

    it('says when a problem was resolved, not just that it was', async () => {
        // The half-fact this panel already refuses to accept for occurredAt, applied to
        // the other timestamp on the same record. resolvedAt is server-derived and has
        // been on the DTO all along; printing the bare word threw away the half that
        // answers "so is this still my problem this morning?".
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e5',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: '2026-08-26T09:00:00Z',
            },
        ]);

        renderPanel();

        await screen.findByText(/3 of 175 disagree/i);
        const when = document.querySelector('time[datetime="2026-08-26T09:00:00Z"]');
        expect(when).not.toBeNull();
        expect(when!.textContent?.trim()).not.toBe('');
    });

    it('does not strike out the row that says the problem was fixed', async () => {
        // Line-through on the <li> propagates to descendants that cannot cancel it,
        // so it would cross out the green "resolved" badge announcing the fix and
        // make the summary — the only thing worth reading — the hardest text on the
        // page to read.
        //
        // A class-name assertion, deliberately: jsdom loads no CSS, so
        // getComputedStyle().textDecorationLine cannot see a Tailwind utility. The
        // regression being pinned (re-decorating the whole row) is worth one brittle
        // string.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e6',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: '2026-08-26T09:00:00Z',
            },
        ]);

        renderPanel();

        const row = (await screen.findByText(/3 of 175 disagree/i)).closest('li');
        expect(row).not.toBeNull();
        expect(row!.className).not.toMatch(/line-through/);
    });

    it('answers "is anything wrong right now" in the head, before the list', async () => {
        // The question the panel is opened to ask. A flat chronological list cannot
        // answer it: a drift fixed last week and one raised a minute ago are the same
        // row shape. The count is the glance-level answer, and it counts only the OPEN
        // ones or it is answering a different question.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e7',
                occurredAt: '2026-08-26T09:00:00Z',
                severity: 'critical',
                topic: 'scheduler',
                eventKey: 'quote-refresh.disabled',
                summary:
                    'Scheduled quote-refresh has been switched OFF after 5 consecutive failures.',
                resolvedAt: null,
            },
            {
                id: 'e8',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: '2026-08-26T09:00:00Z',
            },
        ]);

        renderPanel();

        // One of the two rows is settled, so a count of 2 would be the bug.
        expect(await screen.findByText('1 open')).toBeTruthy();
    });

    it('says "nothing open" rather than staying silent when every problem is fixed', async () => {
        // Not the same claim as the empty state, and it must not be said with the same
        // words: things went wrong here and were fixed. Silence would leave the reader
        // to work out from the rows themselves that none of them is current, which is
        // the work the panel exists to do for them.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e9',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: '2026-08-26T09:00:00Z',
            },
        ]);

        renderPanel();

        expect(await screen.findByText(/nothing open/i)).toBeTruthy();
    });

    it('claims nothing about open problems when the fetch failed or found none', async () => {
        // A count is a claim, and a query that returned nothing has no standing to make
        // one. Anchored on the empty-state copy, which can only render once the query
        // resolved — waiting on the heading would let this pass on the pending frame.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });

        renderPanel();

        await screen.findByText(/no warnings or problems recorded/i);
        expect(screen.queryByText(/nothing open/i)).toBeNull();
        expect(screen.queryByText(/\d+ open/)).toBeNull();
    });

    it('names the subject in English rather than in the API\'s filter key', async () => {
        // `topic` is a contract value — a subscriber filters on it — so it is translated
        // at the point of reading, not renamed at the API. Positive assertion first: the
        // negative below would otherwise pass on the pending frame, when neither string
        // is on screen.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e10',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'consistency',
                eventKey: 'consistency.drift',
                summary: 'realized_gains: 3 of 175 disagree with the transactions.',
                resolvedAt: null,
            },
        ]);

        renderPanelRouted();

        expect(await screen.findByText('Data consistency')).toBeTruthy();
        expect(screen.queryByText('consistency')).toBeNull();
    });

    it('does not dress an unfamiliar subject as one it knows', async () => {
        // The map falls back to the raw value rather than to a blank, so a topic added
        // to the API after this build looks unfamiliar instead of disappearing.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e11',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'warning',
                topic: 'tax-lots',
                eventKey: 'tax-lots.something',
                summary: 'Something happened.',
                resolvedAt: null,
            },
        ]);

        renderPanel();

        expect(await screen.findByText('tax-lots')).toBeTruthy();
    });

    it('does not paint a routine event as a warning', async () => {
        // The latent bug in the two-branch version: anything that was not `critical` was
        // painted amber, so an `info` row would arrive dressed as a problem. The endpoint
        // filters info out today — that is a filter, not a guarantee, and the sibling
        // system panel already has all three branches.
        vi.spyOn(apiModule, 'fetchLedgerNotifications').mockResolvedValue({
            subscribers: [],
            monitorCoverage: {},
            monitorsWatchingNothing: [],
        });
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([
            {
                id: 'e12',
                occurredAt: '2026-08-21T00:46:00Z',
                severity: 'info',
                topic: 'snapshot',
                eventKey: 'snapshot.succeeded',
                summary: 'Scheduled snapshot completed.',
                resolvedAt: null,
            },
        ]);

        renderPanel();

        const severity = await screen.findByText('info');
        expect(severity.className).not.toMatch(/state-warning/);
        expect(severity.className).not.toMatch(/state-danger/);
    });
});
