import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
    createMemoryHistory,
    createRootRoute,
    createRoute,
    createRouter,
    RouterProvider,
} from '@tanstack/react-router';

import { GeneralPanel } from './GeneralPanel';
import * as apiModule from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';

// GeneralPanel — rename + delete (ADR-0020). Locked down:
//   * owner sees enabled rename/delete; Save calls renameLedger
//   * a non-owner (viewer) has both disabled
//   * delete is gated behind a typed-name confirmation before it fires
//   * balance CHECK is read-only and REPAIR only appears once drift is found

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderPanel() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const settingsRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        component: () => <GeneralPanel ledgerId={LEDGER_ID} />,
    });
    const landingRoute = createRoute({
        getParentRoute: () => root,
        path: '/landing-stub',
        component: () => <main>landing</main>,
    });
    const router = createRouter({
        routeTree: root.addChildren([settingsRoute, landingRoute]),
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

/**
 * The panel as reached from a drift notification: same route, arrival flag set.
 */
function renderPanelArrivingFromDriftNotice() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const root = createRootRoute();
    const settingsRoute = createRoute({
        getParentRoute: () => root,
        path: '/',
        validateSearch: (search: Record<string, unknown>): { check?: true } =>
            search.check === true || search.check === 'true' ? { check: true } : {},
        component: () => <GeneralPanel ledgerId={LEDGER_ID} />,
    });
    const router = createRouter({
        routeTree: root.addChildren([settingsRoute]),
        history: createMemoryHistory({ initialEntries: ['/?check=true'] }),
        context: { queryClient },
    });
    const rendered = render(
        <QueryClientProvider client={queryClient}>
            {/* eslint-disable-next-line @typescript-eslint/no-explicit-any */}
            <RouterProvider router={router as any} />
        </QueryClientProvider>,
    );
    return { ...rendered, router };
}

function mockLedger(role: string) {
    const ledgers: LedgerSummary[] = [{ id: LEDGER_ID, name: 'Personal', role }];
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
}

/** One row of the ledger event log, shaped as the API returns it. */
function driftEvent(resolvedAt: string | null) {
    return {
        id: 'evt-1',
        occurredAt: '2026-09-26T04:36:16Z',
        severity: 'warning',
        topic: 'consistency',
        eventKey: 'consistency.drift',
        summary: 'trade_prices: 6 of 6626 disagree with the transactions.',
        resolvedAt,
    };
}

describe('GeneralPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        // The panel reads the event log on mount. Default to a quiet log so the
        // tests that are not about drift are unaffected — and so none of them
        // reaches the network.
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
    });

    it('lets an owner rename the ledger', async () => {
        mockLedger('owner');
        const renameSpy = vi.spyOn(apiModule, 'renameLedger').mockResolvedValue(undefined);
        renderPanel();
        const user = userEvent.setup();

        const input = await screen.findByLabelText(/^name$/i);
        await waitFor(() => expect(input).not.toBeDisabled());
        await user.clear(input);
        await user.type(input, 'Renamed');
        await user.click(screen.getByRole('button', { name: /^rename$/i }));

        await waitFor(() => expect(renameSpy).toHaveBeenCalledWith(LEDGER_ID, 'Renamed'));
    });

    it('disables rename + delete for a non-owner', async () => {
        mockLedger('viewer');
        renderPanel();

        const input = await screen.findByLabelText(/^name$/i);
        expect(input).toBeDisabled();
        expect(screen.getByRole('button', { name: /^rename$/i })).toBeDisabled();
        expect(screen.getByRole('button', { name: /delete ledger/i })).toBeDisabled();
    });

    it('gates delete behind a typed-name confirmation', async () => {
        mockLedger('owner');
        const deleteSpy = vi.spyOn(apiModule, 'deleteLedger').mockResolvedValue(undefined);
        renderPanel();
        const user = userEvent.setup();

        // Open the dialog (only the danger-zone button exists at this point).
        await user.click(await screen.findByRole('button', { name: /delete ledger/i }));

        // Everything below is scoped to the modal so it doesn't collide with
        // the rename input / danger-zone button on the page behind it.
        const dialog = await screen.findByRole('dialog');
        const confirm = within(dialog).getByRole('button', { name: /^delete ledger$/i });
        const typeBox = within(dialog).getByRole('textbox');

        // Gated: confirm is disabled until the ledger name is typed exactly.
        expect(confirm).toBeDisabled();
        await user.type(typeBox, 'Personal');
        await waitFor(() => expect(confirm).toBeEnabled());
        await user.click(confirm);

        await waitFor(() => expect(deleteSpy).toHaveBeenCalledWith(LEDGER_ID));
    });
});

describe('GeneralPanel — consistency maintenance', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    const clean = {
        healthy: true,
        projections: [
            { projection: 'balances', healthy: true, checked: 120, mismatchedCount: 0, mismatches: [] },
            { projection: 'holdings', healthy: true, checked: 8, mismatchedCount: 0, mismatches: [] },
            { projection: 'realized_gains', healthy: true, checked: 8, mismatchedCount: 0, mismatches: [] },
            { projection: 'posting_counts', healthy: true, checked: 60, mismatchedCount: 0, mismatches: [] },
            { projection: 'trade_prices', healthy: true, checked: 42, mismatchedCount: 0, mismatches: [] },
        ],
        unbackedPrices: [],
    };

    // Every projection the report names must be repairable from the UI, and repair
    // must never be the first button available — that pairing is the whole point.
    it('offers a repair for each disagreeing projection, and none when clean', async () => {
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(clean);
        const repair = vi.spyOn(apiModule, 'repairProjection').mockResolvedValue(
            clean.projections[3],
        );

        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        const checkButton = await screen.findByRole('button', { name: /check consistency/i });
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();

        await user.click(checkButton);
        await waitFor(() => expect(check).toHaveBeenCalledWith(LEDGER_ID));

        // A clean report offers nothing to repair.
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
        expect(repair).not.toHaveBeenCalled();

        // Two projections disagreeing → a repair button for each of those two only.
        check.mockResolvedValue({
            unbackedPrices: [],
            healthy: false,
            projections: [
                { projection: 'balances', healthy: true, checked: 120, mismatchedCount: 0, mismatches: [] },
                {
                    projection: 'holdings', healthy: false, checked: 8, mismatchedCount: 1,
                    mismatches: [{
                        scope: 'Brokerage / sec', field: 'cost_basis',
                        stored: 100, expected: 90, diff: -10,
                    }],
                },
                { projection: 'realized_gains', healthy: true, checked: 8, mismatchedCount: 0, mismatches: [] },
                {
                    projection: 'posting_counts', healthy: false, checked: 60, mismatchedCount: 17,
                    mismatches: [{
                        scope: 'header abc', field: 'header_total_postings',
                        stored: 2, expected: 1, diff: -1,
                    }],
                },
            ],
        });
        await user.click(checkButton);

        await screen.findByRole('button', { name: /repair holdings and cost basis/i });
        const postingRepair = screen.getByRole('button', { name: /repair posting counts/i });
        // The healthy ones get no button.
        expect(screen.queryByRole('button', { name: /repair running balances/i })).toBeNull();
        expect(screen.queryByRole('button', { name: /repair realized gains/i })).toBeNull();

        await user.click(postingRepair);
        await waitFor(() =>
            expect(repair).toHaveBeenCalledWith(LEDGER_ID, 'posting_counts'));
    });

    // A projection the API grows without a label here still renders — as its raw
    // key. Nothing breaks, so nothing fails, and the reader is shown a column name.
    it('labels every projection in prose, never as its API key', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(clean);
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        // Anchor on the label so the report has actually rendered before asserting
        // an absence — a queryBy against a frame that has not settled passes for
        // the wrong reason. Each row reads "<label> — healthy, <n> checked", so the
        // matchers are anchored at the start of the row rather than exact.
        expect(await screen.findByText(/^Prices from trades —/)).toBeInTheDocument();
        for (const p of clean.projections) {
            expect(screen.queryByText(new RegExp(`^${p.projection}\b`))).toBeNull();
        }
    });

    // The alert arrives out of band — healthchecks.io, email — and whoever acts on
    // it opens Settings and lands HERE, on the default tab, not on Notifications
    // where the finding was listed. Before this the page looked entirely healthy,
    // which reads as a false alarm and is how a real finding gets ignored.
    it('says so on arrival when the scheduled check reported unresolved drift', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([driftEvent(null)]);
        mockLedger('owner');
        renderPanel();

        const banner = await screen.findByRole('status');
        expect(banner).toHaveTextContent(/reported drift on/i);
        // The monitor's own words, so the reader is not made to guess which figures.
        expect(banner).toHaveTextContent(/trade_prices: 6 of 6626/);
    });

    it('stays quiet once a later run reported the projections healthy', async () => {
        const events = vi
            .spyOn(apiModule, 'fetchLedgerEvents')
            .mockResolvedValue([driftEvent('2026-09-26T16:00:00Z')]);
        mockLedger('owner');
        renderPanel();

        // Settle the frame on the event query itself before asserting an absence:
        // a queryBy against a pending render passes for the wrong reason.
        await waitFor(() => expect(events).toHaveBeenCalledWith(LEDGER_ID));
        await screen.findByRole('button', { name: /check consistency/i });

        expect(screen.queryByRole('status')).toBeNull();
    });

    // "stored 20.3898, expected 0.9868" on a row the reader cannot identify, with
    // one of them hidden behind "…and 1 more", is not enough to decide whether to
    // press Repair. The list has to be complete and the rows have to name something
    // real.
    it('lists every disagreement and says what repair will do', async () => {
        const rows = Array.from({ length: 7 }, (_, i) => ({
            scope: `TICK${i} — Fund ${i} · Brokerage · 2006-01-0${i + 1}`,
            field: "price disagrees with the day's last trade",
            stored: 20.3898,
            expected: 0.9868,
            diff: -19.403,
            accountId: `acct-${i}`,
            headerId: `hdr-${i}`,
        }));
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: false,
            projections: [
                {
                    projection: 'trade_prices',
                    healthy: false,
                    checked: 6626,
                    mismatchedCount: rows.length,
                    mismatches: rows,
                },
            ],
            unbackedPrices: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        // Every row, including the seventh — the old list stopped at five and hid
        // the rest behind "…and N more".
        await screen.findByRole('list');
        const listed = screen
            .getAllByRole('listitem')
            .map((li) => li.textContent ?? '');
        for (const r of rows) {
            expect(listed.some((text) => text.includes(r.scope))).toBe(true);
        }
        expect(screen.queryByText(/and \d+ more/i)).toBeNull();
        // And what the button is about to do, in the reader's terms.
        expect(
            screen.getByText(/never overwritten/i),
        ).toBeInTheDocument();

        // Each row reaches the trade behind the figure — deciding whether to
        // repair means looking at it, and the row already knows where it is.
        const link = screen.getByRole('link', { name: rows[0].scope });
        expect(link).toHaveAttribute(
            'href',
            expect.stringContaining(`/accounts/${rows[0].accountId}`),
        );
        expect(link).toHaveAttribute(
            'href',
            expect.stringContaining(`focus=${rows[0].headerId}`),
        );
    });

    // A price nothing derives is ADVISORY, not a finding: it may be correct,
    // and nothing records which transaction wrote it. So it must not colour the
    // projection, must not be counted among disagreements, and must not offer a
    // button — only a way to go look.
    it('shows unbacked prices as advice, not as a finding', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: true,
            projections: [
                {
                    projection: 'trade_prices',
                    healthy: true,
                    checked: 6540,
                    mismatchedCount: 0,
                    mismatches: [],
                },
            ],
            unbackedPrices: [
                {
                    securityId: 'sec-tdlp',
                    security: 'TDLP — Fidelity GDIT International Equity',
                    count: 2,
                    earliest: '2026-01-07',
                    latest: '2026-05-19',
                    holdingValue: 79516.63,
                },
            ],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        // Linked to the security, which is where the prices are.
        const link = await screen.findByRole('link', {
            name: 'TDLP — Fidelity GDIT International Equity',
        });
        expect(link).toHaveAttribute('href', expect.stringContaining('/securities/sec-tdlp'));

        // Advice, not a defect: the projection stays healthy and offers nothing
        // to press.
        expect(screen.getByText(/healthy/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();

        // And it says what it is worth looking at, in money.
        expect(screen.getByText(/you hold/i)).toBeInTheDocument();
    });

    it('runs the check on arrival from a drift notification, without a click', async () => {
        // The other half of "the finding carries its fix". The scheduled monitor already
        // found the drift; landing on a panel that shows nothing until the reader
        // re-runs the same check by hand is the gap.
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(clean);
        mockLedger('owner');

        renderPanelArrivingFromDriftNotice();

        await waitFor(() => expect(check).toHaveBeenCalledTimes(1));
    });

    it('does not run the check when arriving normally', async () => {
        // The negative half, and the one that matters for cost: the check walks every
        // position, so a plain visit to General settings must not trigger it. Without
        // this, a mount effect that ignored the flag would satisfy the test above.
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(clean);
        mockLedger('owner');

        renderPanel();

        await screen.findByRole('button', { name: /check consistency/i });
        expect(check).not.toHaveBeenCalled();
    });
});
