import { describe, it, expect, beforeEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { formatLedgerDate } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
import type { LedgerConsistencyReport, LedgerSummary } from '@/lib/types';
import type { ProjectionRepairResult, UnbackedPriceAdvisory } from '@/lib/types/ledger';

// GeneralPanel — rename + delete (ADR-0020). Locked down:
//   * owner sees enabled rename/delete; Save calls renameLedger
//   * a non-owner (viewer) has both disabled
//   * delete is gated behind a typed-name confirmation before it fires
//   * balance CHECK is read-only and REPAIR only appears once drift is found

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderPanel(queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
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
    const ledgers: LedgerSummary[] = [{ id: LEDGER_ID, name: 'Personal', role, currencyCode: 'USD' }];
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
}

/** One row of the ledger event log, shaped as the API returns it. */
function driftEvent(resolvedAt: string | null) {
    return {
        id: 'evt-1',
        occurredAt: '2026-09-26T04:00:00Z',
        severity: 'warning',
        topic: 'consistency',
        eventKey: 'consistency.drift',
        summary: 'trade_prices: 6 of 4000 disagree with the transactions.',
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

    it('gives focus to the name field when a rename lands and leaves Rename with nothing to change', async () => {
        let name = 'Personal';
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockImplementation(async () => [{ id: LEDGER_ID, name, role: 'owner', currencyCode: 'USD' }]);
        vi.spyOn(apiModule, 'renameLedger').mockImplementation(async (_id, next) => { name = next; });
        renderPanel();
        const user = userEvent.setup();

        const input = await screen.findByLabelText(/^name$/i);
        await waitFor(() => expect(input).toBeEnabled());
        await user.clear(input);
        await user.type(input, 'Renamed');
        const rename = screen.getByRole('button', { name: /^rename$/i });
        await user.click(rename);

        await waitFor(() => expect(rename).toBeDisabled());
        expect(input).toHaveValue('Renamed');
        await waitFor(() => expect(input).toHaveFocus());
    });

    it.each(['viewer', 'editor'] as const)('offers a %s neither rename nor delete, and reads them the name', async (role) => {
        mockLedger(role);
        renderPanel();

        // Settled: the name, from the read that carries the role, is on the screen.
        const name = await waitFor(() => {
            const el = document.querySelector<HTMLElement>('[data-ledger-name]');
            expect(el).toHaveTextContent('Personal');
            return el!;
        });
        expect(name.tagName).toBe('P');
        expect(screen.queryByLabelText(/^name$/i)).toBeNull();
        expect(screen.queryByRole('button', { name: /^rename$/i })).toBeNull();
        expect(screen.queryByRole('button', { name: /delete ledger/i })).toBeNull();
        expect(screen.queryByText('Danger zone')).toBeNull();
        expect(screen.queryByText('Only an owner can rename this ledger.')).toBeNull();
        expect(screen.getByText('Ledger identity, reporting currency, and maintenance.')).toBeInTheDocument();
    });

    it('offers the owner rename and delete', async () => {
        mockLedger('owner');
        renderPanel();
        expect(await screen.findByLabelText(/^name$/i)).toBeEnabled();
        expect(screen.getByRole('button', { name: /^rename$/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /delete ledger/i })).toBeEnabled();
        expect(screen.getByText('Danger zone')).toBeInTheDocument();
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
            { projection: 'stored_notes', healthy: true, checked: 60, mismatchedCount: 0, mismatches: [] },
            { projection: 'holdings_currency', healthy: true, checked: 2, mismatchedCount: 0, mismatches: [] },
            { projection: 'reminder_auto_post', healthy: true, checked: 1, mismatchedCount: 0, mismatches: [] },
        ],
        unbackedPrices: [],
        oversold: [],
    };

    // Every projection the report names must be repairable from the UI, and repair
    // must never be the first button available — that pairing is the whole point.
    it('offers a repair for each disagreeing projection, and none when clean', async () => {
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(clean);
        const repair = vi.spyOn(apiModule, 'repairProjection').mockResolvedValue({
            projection: 'posting_counts',
            repaired: clean.projections[3],
            notRepaired: [],
            after: clean,
        });

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
        oversold: [],
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
        // `\\b`: in a template literal a single backslash-b is a backspace character,
        // which no row contains, so the absence below would hold for any label.
        for (const p of clean.projections) {
            expect(screen.queryByText(new RegExp(`^${p.projection}\\b`))).toBeNull();
        }
    });

    // The intro says what the check compares. A projection the report shows that the intro
    // does not name is a check the reader was not told runs, and one the intro mis-describes
    // (holdings currency is compared with the brokerage, not with the transactions) is a
    // false claim; each row's label must appear in it.
    it('names every projection the report shows in the intro', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(clean);
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        expect(await screen.findByText(/^Holdings currency —/)).toBeInTheDocument();
        const intro = screen.getByText(/^Compare every stored figure/).textContent ?? '';
        const labels = screen.getAllByText(/ — healthy,/).map(
            (row) => (row.textContent ?? '').split(' — ')[0],
        );
        expect(labels).toHaveLength(clean.projections.length);
        for (const label of labels) {
            expect(intro).toContain(label.toLowerCase());
        }
        expect(intro).toMatch(/holdings currency against the brokerage/);
        // The auto-post row compares a job with the reminders, not with the transactions.
        expect(intro).toMatch(/reminder auto-post job wherever a reminder is set to auto-post/);
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
        expect(banner).toHaveTextContent(/trade_prices: 6 of 4000/);
        // An owner can repair what the check names. The name field is enabled once the
        // reader's role is known, which is what the banner's last sentence waits on too.
        await waitFor(() => expect(screen.getByLabelText(/^name$/i)).toBeEnabled());
        expect(banner).toHaveTextContent(
            'Run the check to see which figures disagree — each one it names can be repaired from here.',
        );
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

    // A stored figure beside an expected one, on a row the reader cannot identify, with
    // one of them hidden behind "…and 1 more", is not enough to decide whether to
    // press Repair. The list has to be complete and the rows have to name something
    // real.
    it('lists every disagreement and says what repair will do', async () => {
        const rows = Array.from({ length: 7 }, (_, i) => ({
            scope: `TICK${i} — Fund ${i} · Brokerage · 2006-01-0${i + 1}`,
            field: "price disagrees with the day's last trade",
            stored: 25.4321,
            expected: 0.9111,
            diff: -24.521,
            accountId: `acct-${i}`,
            headerId: `hdr-${i}`,
        }));
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: false,
            projections: [
                {
                    projection: 'trade_prices',
                    healthy: false,
                    checked: 4000,
                    mismatchedCount: rows.length,
                    mismatches: rows,
                },
            ],
            unbackedPrices: [],
        oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        // Every row, including the seventh — not five and the rest hidden behind
        // "…and N more".
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

    // A running balance stored on a row the register skips has no figure the rule derives:
    // "expected 0" would read as a balance that should be zero, and a link would open a
    // register that cannot show the row. A value finding beside it keeps both.
    it('says a balance stored on a row the register skips is not expected, with no link, beside an unchanged value finding', async () => {
        const valueRow = {
            scope: 'Account A @ 2026-05-01', field: 'balance_after',
            stored: 2, expected: 1, diff: -1,
            accountId: 'acct-a', headerId: 'hdr-value', reason: 'value',
        };
        const extraRow = {
            scope: 'Account A @ 2026-05-15', field: 'balance_after, not derived',
            stored: 777.77, expected: 0, diff: -777.77,
            accountId: 'acct-a', headerId: 'hdr-extra', reason: 'extra',
        };
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: false,
            projections: [{
                projection: 'balances', healthy: false, checked: 12, mismatchedCount: 2,
                mismatches: [valueRow, extraRow],
            }],
            unbackedPrices: [],
            oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        // Anchored on the row's own scope before anything is asserted absent from it.
        const extra = (await screen.findByText(new RegExp(`^${extraRow.scope}`))).closest('li')!;
        expect(extra).toHaveTextContent(`${extraRow.scope} · ${extraRow.field}: stored 777.77, none expected`);
        expect(extra).not.toHaveTextContent(/expected 0/);
        expect(within(extra).queryByRole('link')).toBeNull();

        // The value finding is as it was: its figures and the link to the row behind it.
        const link = screen.getByRole('link', { name: valueRow.scope });
        expect(link).toHaveAttribute('href', expect.stringContaining(`focus=${valueRow.headerId}`));
        expect(link.closest('li')!).toHaveTextContent(`${valueRow.scope} · ${valueRow.field}: stored 2, expected 1`);

        // And the repair says it removes such a balance and changes no transaction.
        expect(screen.getByText(/removes any running balance stored on a row the register skips/)).toHaveTextContent(
            /It does not change any transaction\.$/,
        );
    });

    // A currency finding has no figure to compare: the row names both currencies in its
    // field and prints no "stored, expected", and the repair says it converts nothing.
    it('names both currencies of a currency finding and says what its repair does', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: false,
            projections: [
                {
                    projection: 'trade_prices', healthy: false, checked: 1, mismatchedCount: 1,
                    mismatches: [{
                        scope: 'SECA — Security A · Brokerage A · 2026-05-01',
                        field: "price in USD, the day's last trade in EUR",
                        stored: 0, expected: 0, diff: 0,
                        accountId: 'acct-a', headerId: 'hdr-a', reason: 'value',
                    }],
                },
                {
                    projection: 'holdings_currency', healthy: false, checked: 2, mismatchedCount: 1,
                    mismatches: [{
                        scope: 'Brokerage B',
                        field: 'its holdings are recorded in USD, but the brokerage is in EUR',
                        stored: 0, expected: 0, diff: 0,
                        accountId: 'acct-b',
                    }],
                },
            ],
            unbackedPrices: [],
            oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        // The holdings currency's rows are brokerages, counted in the reader's terms.
        const title = await screen.findByText(/^Holdings currency —/);
        expect(title.textContent).toBe('Holdings currency — 1 of 2 brokerages out of step');
        const listed = screen.getAllByRole('listitem').map((li) => li.textContent ?? '');
        expect(listed).toContain('Brokerage B · its holdings are recorded in USD, but the brokerage is in EUR');
        expect(listed).toContain("SECA — Security A · Brokerage A · 2026-05-01 · price in USD, the day's last trade in EUR");
        expect(listed.some((text) => /stored/.test(text))).toBe(false);
        // The holdings repair says why the finding is there, and that it relabels: it
        // converts nothing and writes no price.
        expect(screen.getByText(
            "This happens when a brokerage's currency was changed in an earlier version. "
            + "Repair records each brokerage's holdings in its own currency. "
            + 'Nothing is converted, and no price or transaction changes.',
        )).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Repair holdings currency' })).toBeInTheDocument();
        // The trade-price repair writes the currency of the brokerage that made the trade,
        // whatever the holdings account is kept in, and it lists no unbacked price: those
        // are the advisory's.
        const tradeEffect = screen.getByText(/in the currency of the brokerage that made that trade/);
        expect(tradeEffect.textContent).toMatch(/listed under Prices worth a look/);
        expect(tradeEffect.textContent).not.toMatch(/holdings account|Holdings currency|NOT repaired here/);
    });

    // The ledger's one auto-post job is what this row examines: it says the job is not set
    // up rather than "1 of 1 disagree", prints the server's line with no figures, and says
    // why the finding is there and what its repair does.
    it('says a missing auto-post job is not set up and what its repair does', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: false,
            projections: [
                {
                    projection: 'reminder_auto_post', healthy: false, checked: 1, mismatchedCount: 1,
                    mismatches: [{
                        scope: 'Auto-post job',
                        field: 'not set up, so the 2 reminders set to auto-post are never posted',
                        stored: 0, expected: 0, diff: 0,
                    }],
                },
            ],
            unbackedPrices: [],
            oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        const title = await screen.findByText(/^Reminder auto-post —/);
        expect(title.textContent).toBe('Reminder auto-post — the auto-post job is not set up');
        const listed = screen.getAllByRole('listitem').map((li) => li.textContent ?? '');
        expect(listed).toEqual(['Auto-post job · not set up, so the 2 reminders set to auto-post are never posted']);
        expect(screen.getByText(
            'This happens when a reminder set to auto-post was imported from Moneydance, or saved, '
            + 'by an earlier version that did not turn the job on. Repair turns the job on at its '
            + 'default time, as saving such a reminder does; the Reminders page changes its time. '
            + 'From its next run the job posts what is due, including occurrences '
            + 'already past due within its catch-up window that nobody posted or skipped; post or '
            + 'skip those on the Reminders page first if you do not want them posted. Repair itself '
            + 'changes no reminder or transaction.',
        )).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Repair reminder auto-post' })).toBeInTheDocument();
    });

    it('counts one brokerage checked as "brokerage"', async () => {
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: false,
            projections: [
                {
                    projection: 'holdings_currency', healthy: false, checked: 1, mismatchedCount: 1,
                    mismatches: [{
                        scope: 'Brokerage C',
                        field: 'its holdings are recorded in USD, but the brokerage is in GBP',
                        stored: 0, expected: 0, diff: 0,
                        accountId: 'acct-c',
                    }],
                },
            ],
            unbackedPrices: [],
            oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        const title = await screen.findByText(/^Holdings currency —/);
        expect(title.textContent).toBe('Holdings currency — 1 of 1 brokerage out of step');
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
                    checked: 3900,
                    mismatchedCount: 0,
                    mismatches: [],
                },
            ],
            unbackedPrices: [
                {
                    securityId: 'sec-fund-b',
                    security: 'FUNDB — Fund B',
                    count: 2,
                    earliest: '2026-01-07',
                    latest: '2026-05-19',
                    holdingValue: 45678.90,
                    currencyCode: 'USD',
                    rateUnavailable: [],
                },
            ],
            oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        // Linked to the security, which is where the prices are.
        const link = await screen.findByRole('link', {
            name: 'FUNDB — Fund B',
        });
        expect(link).toHaveAttribute('href', expect.stringContaining('/securities/sec-fund-b'));

        // Advice, not a defect: the projection stays healthy and offers nothing
        // to press.
        expect(screen.getByText(/healthy/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();

        // And it says what it is worth looking at, in money.
        expect(screen.getByText(/you hold/i)).toBeInTheDocument();
    });

    it('says what an unbacked price\'s holding is worth in the reporting currency, or that its rate is missing', async () => {
        const advisory = (securityId: string, security: string, over: Partial<UnbackedPriceAdvisory>): UnbackedPriceAdvisory => ({
            securityId, security, count: 1, earliest: '2026-01-07', latest: '2026-01-07',
            holdingValue: 1200, currencyCode: 'EUR', rateUnavailable: [], ...over,
        });
        const missing = { fromCurrency: 'GBP', toCurrency: 'EUR', date: '2026-01-07', reason: 'rate_unavailable' as const };
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: true,
            projections: [],
            unbackedPrices: [
                advisory('sec-a', 'FUNDA — Fund A', {}),
                advisory('sec-b', 'FUNDB — Fund B', { holdingValue: null, rateUnavailable: [missing] }),
                advisory('sec-c', 'FUNDC — Fund C', { holdingValue: 0 }),
            ],
            oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        const row = (name: string) => screen.getByRole('link', { name }).closest('li')!;
        await screen.findByRole('link', { name: 'FUNDA — Fund A' });
        expect(row('FUNDA — Fund A')).toHaveTextContent(`you hold ${formatCurrency(1200, 'EUR')}`);
        // A value that needs a missing rate is no figure, so none is printed.
        expect(row('FUNDB — Fund B')).toHaveTextContent('still held, no rate');
        expect(within(row('FUNDB — Fund B')).getByText('no rate')).toHaveAttribute('title', 'No GBP to EUR rate for Jan 7, 2026.');
        expect(row('FUNDB — Fund B').textContent).not.toContain('you hold');
        expect(row('FUNDC — Fund C')).toHaveTextContent('no longer held');
        // None of the three is in dollars.
        for (const name of ['FUNDA — Fund A', 'FUNDB — Fund B', 'FUNDC — Fund C']) {
            expect(row(name).textContent).not.toContain('$');
        }
    });

    // The API sends an unbacked price's days as calendar dates (YYYY-MM-DD); they print as
    // every other ledger date does, never as the raw value.
    it('prints an unbacked price\'s days as every other ledger date prints', async () => {
        const advisory = (securityId: string, security: string, earliest: string, latest: string): UnbackedPriceAdvisory => ({
            securityId, security, count: 2, earliest, latest,
            holdingValue: 1200, currencyCode: 'USD', rateUnavailable: [],
        });
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: true,
            projections: [],
            unbackedPrices: [
                advisory('sec-a', 'FUNDA — Fund A', '2026-01-07', '2026-05-19'),
                advisory('sec-b', 'FUNDB — Fund B', '2026-12-01', '2026-12-01'),
            ],
            oversold: [],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        const row = (name: string) => screen.getByRole('link', { name }).closest('li')!;
        await screen.findByRole('link', { name: 'FUNDA — Fund A' });
        expect(row('FUNDA — Fund A')).toHaveTextContent(
            `, ${formatLedgerDate('2026-01-07')} to ${formatLedgerDate('2026-05-19')}`);
        expect(row('FUNDB — Fund B')).toHaveTextContent(`on ${formatLedgerDate('2026-12-01')}`);
        for (const raw of ['2026-01-07', '2026-05-19']) expect(row('FUNDA — Fund A').textContent).not.toContain(raw);
        expect(row('FUNDB — Fund B').textContent).not.toContain('2026-12-01');
    });

    it('lists oversold positions as findings with a link to fix them and no repair', async () => {
        // FU-097: a sale that took more shares than were held. Nothing can repair it,
        // because the missing purchase is not in the data, so it gets a link to the
        // register where the sale is, and no button.
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            healthy: false,
            projections: [
                { projection: 'holdings', healthy: true, checked: 8, mismatchedCount: 0, mismatches: [] },
            ],
            unbackedPrices: [],
            oversold: [
                {
                    accountId: 'acct-brokerage',
                    account: 'Brokerage',
                    securityId: 'sec-idx',
                    security: 'IDX — Index Fund',
                    sharesShort: 2,
                },
            ],
        });
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        const link = await screen.findByRole('link', { name: 'Brokerage · IDX — Index Fund' });
        expect(link).toHaveAttribute('href', expect.stringContaining('/accounts/acct-brokerage'));
        expect(screen.getByText(/sold 2(\.0+)? more shares than were held/)).toBeInTheDocument();
        expect(screen.getByText(/Correct the sale, or enter the purchase it is missing/)).toBeInTheDocument();
        // Anchored on the link above, so the report has rendered: nothing to repair.
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
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

    // A double press lands both presses before TanStack's notify timeout lets
    // Checking… render: the first press holds the button until its check has
    // settled, so the second sends nothing (ADR-0023 §L, pending.ts).
    it('runs one check for a double press of Check consistency', async () => {
        let answer!: () => void;
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency')
            .mockImplementation(() => new Promise((resolve) => { answer = () => resolve(clean); }));
        mockLedger('owner');
        renderPanel();
        const button = await screen.findByRole('button', { name: /check consistency/i });

        fireEvent.click(button);
        fireEvent.click(button);
        for (let i = 0; i < 10; i += 1) await Promise.resolve();
        expect(button).not.toHaveAttribute('aria-busy');
        expect(check).toHaveBeenCalledTimes(1);

        await waitFor(() => expect(button).toHaveAccessibleName('Checking…'));
        answer();
        await waitFor(() => expect(button).toHaveAccessibleName('Check consistency'));
        expect(check).toHaveBeenCalledTimes(1);
    });

    // It sits right-aligned beside its description: changing size as it reads
    // Checking… would move its left edge from under the pointer, and the second
    // press of a double click would land on the text beside it. jsdom lays out
    // nothing, so what holds its size is asserted: both labels are in it from
    // the first render, the one not shown hidden (PendingLabel).
    it('holds both of Check consistency\'s labels from the first render, so it keeps its size', async () => {
        let answer!: () => void;
        vi.spyOn(apiModule, 'checkLedgerConsistency')
            .mockImplementation(() => new Promise((resolve) => { answer = () => resolve(clean); }));
        mockLedger('owner');
        renderPanel();
        const button = await screen.findByRole('button', { name: /check consistency/i });
        const checking = within(button).getByText('Checking…');
        expect(checking).toHaveAttribute('aria-hidden', 'true');

        fireEvent.click(button);
        await waitFor(() => expect(button).toHaveAccessibleName('Checking…'));
        expect(within(button).getByText('Checking…')).toBe(checking);
        expect(within(button).getByText('Check consistency')).toHaveAttribute('aria-hidden', 'true');

        answer();
        await waitFor(() => expect(button).toHaveAccessibleName('Check consistency'));
    });

    // The check records its verdict, which the Overview's Problems widget, its
    // "checked" time, the navigation's dots and the event log all read: each refetches
    // once the check answers, or they keep saying what an older check found.
    it('refreshes the reads that show the verdict once a check answers', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(clean);
        mockLedger('owner');
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
        renderPanel(queryClient);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        await screen.findByText(/^Holdings currency —/);

        const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey));
        expect(keys).toEqual(expect.arrayContaining([
            JSON.stringify(['home', LEDGER_ID, 'consistency-state']),
            JSON.stringify(['home', LEDGER_ID, 'outstanding']),
            JSON.stringify(['ledger-attention']),
            JSON.stringify(['ledger-events', LEDGER_ID]),
        ]));
        // A check changes no figure, so nothing of the register refetches.
        expect(keys).not.toContain(JSON.stringify(['register', LEDGER_ID]));
    });

    // A repair answers every projection checked again: that report replaces the one
    // on screen, and the ledger is not walked a second time by a check behind it.
    it('shows the report a repair answers, without checking again', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        const drifted = {
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount: 1,
                    mismatches: [{ scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        };
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(drifted);
        vi.spyOn(apiModule, 'repairProjection').mockResolvedValue({
            projection: 'posting_counts',
            repaired: drifted.projections[3],
            notRepaired: [],
            after: clean,
        });
        mockLedger('owner');
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
        renderPanel(queryClient);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        await user.click(await screen.findByRole('button', { name: /repair posting counts/i }));

        expect(await screen.findByText(/^Posting counts — healthy/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
        expect(check).toHaveBeenCalledTimes(1);
        const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey));
        expect(keys).toEqual(expect.arrayContaining([
            JSON.stringify(['register', LEDGER_ID]),
            JSON.stringify(['ledger-attention']),
            JSON.stringify(['ledger-events', LEDGER_ID]),
        ]));
    });

    // A repair whose check did not finish answers what it rewrote and no report: the
    // report on screen was taken before the repair, so it goes, and the panel says the
    // repair stands and what still disagrees is not known. The register still refetches.
    it('says a repair stands when its check did not finish, and drops the report taken before it', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        const drifted = {
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount: 1,
                    mismatches: [{ scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        };
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(drifted);
        vi.spyOn(apiModule, 'repairProjection').mockResolvedValue({
            projection: 'posting_counts',
            repaired: drifted.projections[3],
            notRepaired: [],
            after: null,
        });
        mockLedger('owner');
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
        renderPanel(queryClient);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Posting counts — /)).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: /repair posting counts/i }));

        expect(await screen.findByRole('status')).toHaveTextContent(
            'Repaired Posting counts. The check after the repair did not finish, so what still '
            + 'disagrees is not known until the ledger is checked again.',
        );
        expect(screen.queryByText(/^Posting counts — /)).toBeNull();
        expect(screen.queryByRole('alert')).toBeNull();
        const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey));
        expect(keys).toContain(JSON.stringify(['register', LEDGER_ID]));

        // The next check says what is known again.
        await user.click(screen.getByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Posting counts — /)).toBeInTheDocument();
        expect(screen.queryByRole('status')).toBeNull();
    });

    // A repair that fails partway answers an error, but what it rewrote before failing
    // stands and the server clears the verdict taken before it: the register and the reads
    // that show a verdict refetch, or they keep saying what the data was before the repair.
    it('refreshes the register and the verdict reads when a repair fails', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        const drifted = {
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount: 2,
                    mismatches: [{ scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        };
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(drifted);
        vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(
            new apiModule.ApiError(500, 'An error occurred while processing your request.'),
        );
        mockLedger('owner');
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        renderPanel(queryClient);
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Posting counts — /)).toBeInTheDocument();
        const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
        await user.click(screen.getByRole('button', { name: /repair posting counts/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent('An error occurred while processing your request.');
        const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey));
        expect(keys).toEqual(expect.arrayContaining([
            JSON.stringify(['register', LEDGER_ID]),
            JSON.stringify(['home', LEDGER_ID, 'consistency-state']),
            JSON.stringify(['home', LEDGER_ID, 'outstanding']),
            JSON.stringify(['ledger-attention']),
            JSON.stringify(['ledger-events', LEDGER_ID]),
        ]));
    });

    // Part of what a repair that fails partway rewrote may stand, so the report taken before
    // it may no longer describe the data: it goes, with its Repair buttons, as it does for a
    // repair whose check did not finish, and the error stays shown. The next check brings a
    // report, and its Repair buttons, back.
    it('drops the report taken before a repair that fails, and says part of it may stand', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        const drifted = (mismatchedCount: number) => ({
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount,
                    mismatches: [{ scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        });
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency')
            .mockResolvedValueOnce(drifted(2))
            .mockResolvedValueOnce(drifted(1));
        vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(
            new apiModule.ApiError(500, 'An error occurred while processing your request.'),
        );
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        await user.click(screen.getByRole('button', { name: /repair posting counts/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent('An error occurred while processing your request.');
        expect(screen.getByRole('status')).toHaveTextContent(
            'The repair of posting counts did not finish, and part of it may stand. '
            + 'Check consistency shows what still disagrees.',
        );
        expect(screen.queryByText(/^Posting counts — /)).toBeNull();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();

        // The next check answers a fresh report, with its Repair buttons, and that report
        // replaces the repair's error as well as its status.
        await user.click(screen.getByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 1 of 60 disagree');
        expect(screen.getByRole('button', { name: /repair posting counts/i })).toBeInTheDocument();
        expect(screen.queryByRole('status')).toBeNull();
        expect(screen.queryByRole('alert')).toBeNull();
        expect(check).toHaveBeenCalledTimes(2);
    });

    // A check that fails after a repair that failed shows its own error, not the repair's
    // as well: the repair's status went at the press, and its error would stay with nothing
    // to say which action it came from.
    it('shows only the check\'s error when a check after a failed repair fails', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency')
            .mockResolvedValueOnce(postingCountsDrifted(2))
            .mockRejectedValueOnce(new apiModule.ApiError(503, 'Service Unavailable'));
        vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(
            new apiModule.ApiError(500, 'An error occurred while processing your request.'),
        );
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        const checkButton = await screen.findByRole('button', { name: /check consistency/i });
        await user.click(checkButton);
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        await user.click(screen.getByRole('button', { name: /repair posting counts/i }));
        expect(await screen.findByRole('alert')).toHaveTextContent('An error occurred while processing your request.');

        await user.click(checkButton);
        expect(await screen.findByText('Service Unavailable')).toHaveAttribute('role', 'alert');
        expect(screen.getAllByRole('alert').map((a) => a.textContent)).toEqual(['Service Unavailable']);
        expect(screen.queryByRole('status')).toBeNull();
    });

    /** A report with posting counts out of step, `mismatchedCount` of them. */
    function postingCountsDrifted(mismatchedCount: number) {
        return {
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount,
                    mismatches: [{ scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        };
    }

    // A check that began while a repair rewrote would read the ledger partway through the
    // rewrite, and, answering after the repair, would show that report, with live Repair
    // buttons, beside the repair's status, and the server holds that same verdict back.
    // Check consistency waits on the repair in flight, as every Repair does: pending, and
    // still reading "Check consistency", since "Checking…" is its own check's.
    it('holds Check consistency while a repair is in flight', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        let answerCheck: (() => void) | undefined;
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency')
            .mockResolvedValueOnce(postingCountsDrifted(2))
            .mockImplementation(() => new Promise((resolve) => {
                answerCheck = () => resolve(postingCountsDrifted(1));
            }));
        let failRepair!: () => void;
        vi.spyOn(apiModule, 'repairProjection').mockImplementation(() => new Promise((_resolve, reject) => {
            failRepair = () => reject(new apiModule.ApiError(500, 'An error occurred while processing your request.'));
        }));
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        const checkButton = await screen.findByRole('button', { name: /check consistency/i });
        await user.click(checkButton);
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        const repairButton = screen.getByRole('button', { name: /repair posting counts/i });
        await user.click(repairButton);

        // Repairing… renders from the same pending state the check waits on.
        await waitFor(() => expect(repairButton).toHaveAccessibleName('Repairing…'));
        expect(checkButton).toHaveAttribute('aria-disabled', 'true');
        expect(checkButton).toHaveAccessibleName('Check consistency');
        await user.click(checkButton);
        expect(check).toHaveBeenCalledTimes(1);

        failRepair();
        expect(await screen.findByRole('status')).toHaveTextContent(
            'The repair of posting counts did not finish, and part of it may stand.',
        );
        answerCheck?.();
        await waitFor(() => expect(checkButton).not.toHaveAttribute('aria-disabled'));
        expect(screen.queryByText(/^Posting counts — /)).toBeNull();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
        expect(check).toHaveBeenCalledTimes(1);
    });

    // A press lands before the repair's pending state renders (TanStack notifies on a
    // later task), so the hold is taken at the press: Check consistency and the Repair
    // buttons are one group, and a press of Check consistency right behind a press of
    // Repair sends no check (pending.ts).
    it('sends no check for a press of Check consistency right behind a press of Repair', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        const check = vi.spyOn(apiModule, 'checkLedgerConsistency')
            .mockResolvedValueOnce(postingCountsDrifted(2))
            .mockResolvedValue(postingCountsDrifted(1));
        let answerRepair!: () => void;
        vi.spyOn(apiModule, 'repairProjection').mockImplementation(() => new Promise((resolve) => {
            answerRepair = () => resolve({
                projection: 'posting_counts',
                repaired: postingCountsDrifted(2).projections[3],
                notRepaired: [],
                after: clean,
            });
        }));
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        const checkButton = await screen.findByRole('button', { name: /check consistency/i });
        await user.click(checkButton);
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');

        fireEvent.click(screen.getByRole('button', { name: /repair posting counts/i }));
        fireEvent.click(checkButton);
        for (let i = 0; i < 10; i += 1) await Promise.resolve();
        expect(check).toHaveBeenCalledTimes(1);

        answerRepair();
        expect(await screen.findByText(/^Posting counts — healthy/)).toBeInTheDocument();
        expect(check).toHaveBeenCalledTimes(1);
    });

    // An error the server did not send (the connection dropped, or a proxy answered in its
    // place) says nothing of the repair, which the server carries on to the end whatever
    // happens to the client. The report taken before it goes, as for a repair that failed,
    // but the panel says no answer came back, that the repair may still be running and is
    // recorded when it finishes, not that it did not finish.
    it.each([
        ['the connection drops', new TypeError('Failed to fetch')],
        ['a proxy times out', new apiModule.ApiError(504, 'Gateway Timeout')],
    ])('says a repair may still be running when %s', async (_case, failure) => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'balances'
                ? {
                    ...p, healthy: false, mismatchedCount: 1,
                    mismatches: [{ scope: 'Checking · 2026-05-01', field: 'balance_after', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        });
        vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(failure);
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Running balances — /)).toHaveTextContent('Running balances — 1 of 120 disagree');
        await user.click(screen.getByRole('button', { name: /repair running balances/i }));

        expect(await screen.findByRole('status')).toHaveTextContent(
            'No answer came back from the repair of running balances. It may still be running, '
            + 'and it is recorded when it finishes. Check consistency shows where things stand.',
        );
        expect(screen.getByRole('alert')).toHaveTextContent(failure.message);
        expect(screen.queryByText(/^Running balances — /)).toBeNull();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
    });

    /**
     * A repair that answers only when the test says, so the press renders as pending first,
     * with focus on the Repair pressed.
     */
    function deferRepair() {
        let settle!: {
            resolve: (result: ProjectionRepairResult) => void;
            reject: (error: unknown) => void;
        };
        vi.spyOn(apiModule, 'repairProjection').mockImplementation(() => new Promise((resolve, reject) => {
            settle = { resolve, reject };
        }));
        return {
            resolve: (result: ProjectionRepairResult) => settle.resolve(result),
            reject: (error: unknown) => settle.reject(error),
        };
    }

    /** Presses the Repair named, and waits until it is pending with focus on it. */
    async function pressRepair(user: ReturnType<typeof userEvent.setup>, name: RegExp) {
        const repairButton = screen.getByRole('button', { name });
        await user.click(repairButton);
        await waitFor(() => expect(repairButton).toHaveAccessibleName('Repairing…'));
        expect(repairButton).toHaveFocus();
        return repairButton;
    }

    /**
     * `report` with running balances out of step, one balance listed, which links to no
     * transaction. Running balances is the first row drawn.
     */
    function withBalancesDrifted(report: LedgerConsistencyReport): LedgerConsistencyReport {
        return {
            ...report,
            healthy: false,
            projections: report.projections.map((p) => p.projection === 'balances'
                ? {
                    ...p, healthy: false, mismatchedCount: 1,
                    mismatches: [{ scope: 'Checking · 2026-05-01', field: 'balance_after', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        };
    }

    /**
     * `report` with posting counts out of step, one header listed, which with `linked`
     * links to its transaction. Posting counts is drawn after running balances.
     */
    function withPostingCountsDrifted(report: LedgerConsistencyReport, linked: boolean): LedgerConsistencyReport {
        return {
            ...report,
            healthy: false,
            projections: report.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount: 1,
                    mismatches: [{
                        scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1,
                        ...(linked ? { accountId: 'acct-a', headerId: 'hdr-a' } : {}),
                    }],
                }
                : p),
        };
    }

    /** Each way a repair of posting counts takes the report away, and what the panel then says. */
    const reportGoes: [string, (repair: ReturnType<typeof deferRepair>) => void, RegExp][] = [
        [
            'fails partway',
            (repair) => repair.reject(new apiModule.ApiError(500, 'An error occurred while processing your request.')),
            /^The repair of posting counts did not finish/,
        ],
        [
            'answers nothing',
            (repair) => repair.reject(new TypeError('Failed to fetch')),
            /^No answer came back from the repair of posting counts/,
        ],
        [
            'finishes while its check does not',
            (repair) => repair.resolve({
                projection: 'posting_counts',
                repaired: postingCountsDrifted(2).projections[3],
                notRepaired: [],
                after: null,
            }),
            /^Repaired Posting counts\. The check after the repair did not finish/,
        ],
    ];

    // A repair that fails partway, answers nothing, or whose check did not finish takes the
    // report away, and the Repair pressed with it while it holds focus. Focus goes to Check
    // consistency, the step the panel names next, never to the page (ADR-0023 §L).
    it.each(reportGoes)('gives focus to Check consistency when a repair that %s takes the report away', async (_case, settle, said) => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(postingCountsDrifted(2));
        const repair = deferRepair();
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        const checkButton = await screen.findByRole('button', { name: /check consistency/i });
        await user.click(checkButton);
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        await pressRepair(user, /repair posting counts/i);

        settle(repair);
        expect(await screen.findByRole('status')).toHaveTextContent(said);
        expect(screen.queryByText(/^Posting counts — /)).toBeNull();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
        expect(checkButton).toHaveFocus();
    });

    // Focus the user has moved on to another control in the report goes with the report
    // the same way: from another Repair, pending and still a Tab stop, or from a link to
    // what disagrees, to Check consistency, as a report's Save as… form hands focus on from
    // whichever of its controls held it.
    const insideTheReport: [string, boolean, () => HTMLElement][] = [
        ['another Repair', false, () => screen.getByRole('button', { name: /repair running balances/i })],
        ['a link to what disagrees', true, () => screen.getByRole('link', { name: 'header abc' })],
    ];
    it.each(insideTheReport.flatMap(([where, linked, target]) =>
        reportGoes.map(([how, settle, said]) => [where, how, linked, target, settle, said] as const)))(
        'gives focus to Check consistency from %s when a repair that %s takes the report away',
        async (_where, _how, linked, target, settle, said) => {
            const repair = deferRepair();
            const user = await renderTwoDrifted(linked);
            const pressed = await pressRepair(user, /repair posting counts/i);
            // Back from the Repair pressed: its row's link when the row has one, else the
            // Repair of the row above.
            await user.tab({ shift: true });
            expect(target()).toHaveFocus();
            expect(target()).not.toBe(pressed);

            settle(repair);
            expect(await screen.findByRole('status')).toHaveTextContent(said);
            expect(screen.queryByText(/^Posting counts — /)).toBeNull();
            expect(screen.queryByRole('link', { name: 'header abc' })).toBeNull();
            expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
            expect(screen.getByRole('button', { name: /check consistency/i })).toHaveFocus();
        },
    );

    // A repair that heals its figures keeps the row and takes its own Repair away. Focus
    // goes to the next Repair drawn, else the previous one, else Check consistency, as a
    // row's control hands focus on (ADR-0023 §L). With no other Repair left, focus goes
    // to Check consistency.
    it('gives focus to Check consistency when a repair heals the last figures that disagreed', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(postingCountsDrifted(2));
        const repair = deferRepair();
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        const checkButton = await screen.findByRole('button', { name: /check consistency/i });
        await user.click(checkButton);
        expect(await screen.findByText(/^Posting counts — /)).toBeInTheDocument();
        await pressRepair(user, /repair posting counts/i);

        repair.resolve({ projection: 'posting_counts', repaired: postingCountsDrifted(2).projections[3], notRepaired: [], after: clean });
        expect(await screen.findByText(/^Posting counts — healthy/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
        expect(checkButton).toHaveFocus();
    });

    /**
     * The panel for an owner, checked, with running balances and posting counts out of
     * step, posting counts' header linked to its transaction with `linked`.
     */
    async function renderTwoDrifted(linked = false) {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(
            withBalancesDrifted(withPostingCountsDrifted(clean, linked)),
        );
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();
        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Running balances — /)).toHaveTextContent('Running balances — 1 of 120 disagree');
        expect(screen.getByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 1 of 60 disagree');
        return user;
    }

    // The healed row was drawn before another Repair: focus goes to that next Repair.
    it('gives focus to the next Repair when a repair heals its figures', async () => {
        const repair = deferRepair();
        const user = await renderTwoDrifted();
        await pressRepair(user, /repair running balances/i);

        repair.resolve({
            projection: 'balances',
            repaired: withBalancesDrifted(clean).projections[0],
            notRepaired: [],
            after: withPostingCountsDrifted(clean, false),
        });
        expect(await screen.findByText(/^Running balances — healthy/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /repair running balances/i })).toBeNull();
        expect(screen.getByRole('button', { name: /repair posting counts/i })).toHaveFocus();
    });

    // The healed row was the last one with a Repair: focus goes to the previous Repair.
    it('gives focus to the previous Repair when a repair heals the figures of the last Repair drawn', async () => {
        const repair = deferRepair();
        const user = await renderTwoDrifted();
        await pressRepair(user, /repair posting counts/i);

        repair.resolve({
            projection: 'posting_counts',
            repaired: withPostingCountsDrifted(clean, false).projections[3],
            notRepaired: [],
            after: withBalancesDrifted(clean),
        });
        expect(await screen.findByText(/^Posting counts — healthy/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /repair posting counts/i })).toBeNull();
        expect(screen.getByRole('button', { name: /repair running balances/i })).toHaveFocus();
    });

    // The healed row's list of what disagrees goes with its Repair, and a link in it hands
    // focus on as the Repair would.
    it('gives focus to the previous Repair from a link the heal takes away', async () => {
        const repair = deferRepair();
        const user = await renderTwoDrifted(true);
        await pressRepair(user, /repair posting counts/i);
        await user.tab({ shift: true });
        expect(screen.getByRole('link', { name: 'header abc' })).toHaveFocus();

        repair.resolve({
            projection: 'posting_counts',
            repaired: withPostingCountsDrifted(clean, true).projections[3],
            notRepaired: [],
            after: withBalancesDrifted(clean),
        });
        expect(await screen.findByText(/^Posting counts — healthy/)).toBeInTheDocument();
        expect(screen.queryByRole('link', { name: 'header abc' })).toBeNull();
        expect(screen.getByRole('button', { name: /repair running balances/i })).toHaveFocus();
    });

    // Focus the user has moved elsewhere before the heal stays where it is: on Check
    // consistency, pending and still a Tab stop, or outside the report.
    it.each([
        [
            'Check consistency',
            async (user: ReturnType<typeof userEvent.setup>) => {
                // Back from the first row's Repair, whose row has no link.
                await user.tab({ shift: true });
                return screen.getByRole('button', { name: /check consistency/i });
            },
        ],
        [
            'the ledger name, outside the report',
            async (user: ReturnType<typeof userEvent.setup>) => {
                const nameField = screen.getByLabelText(/^name$/i);
                await waitFor(() => expect(nameField).toHaveValue('Personal'));
                await user.click(nameField);
                return nameField;
            },
        ],
    ])('leaves focus on %s when a repair heals its figures', async (_where, moveFocus) => {
        const repair = deferRepair();
        const user = await renderTwoDrifted();
        await pressRepair(user, /repair running balances/i);
        const target = await moveFocus(user);
        expect(target).toHaveFocus();

        repair.resolve({
            projection: 'balances',
            repaired: withBalancesDrifted(clean).projections[0],
            notRepaired: [],
            after: withPostingCountsDrifted(clean, false),
        });
        expect(await screen.findByText(/^Running balances — healthy/)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /repair running balances/i })).toBeNull();
        expect(screen.getByRole('button', { name: /repair posting counts/i })).toBeInTheDocument();
        expect(target).toHaveFocus();
    });

    // A repair refused before it starts answers a 4xx and rewrites nothing, so the report
    // taken before it still describes the data: it stays, with its Repair buttons, the
    // refusal is shown, and the panel says nothing of a repair that did not finish. Another
    // repair of the ledger still running, one whose answer never arrived among them, is one
    // such refusal.
    it.each([
        [
            'the role cannot write',
            'This ledger is read-only for you. Ask an owner for editor or owner access.',
            'ledger-not-writable',
        ],
        [
            'another repair of the ledger is running',
            'A repair of this ledger is already running. Wait for it to finish before starting another.',
            'consistency-repair-in-progress',
        ],
    ])('keeps the report when a repair is refused before it starts because %s', async (_case, refusal, code) => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount: 2,
                    mismatches: [{ scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        });
        const repair = vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(
            new apiModule.ApiError(422, refusal, code),
        );
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        await user.click(screen.getByRole('button', { name: /repair posting counts/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent(refusal);
        expect(repair).toHaveBeenCalledTimes(1);
        expect(screen.getByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        expect(screen.getByRole('button', { name: /repair posting counts/i })).toBeInTheDocument();
        expect(screen.queryByRole('status')).toBeNull();
    });

    // A repair stopped because another change to the ledger held it past the wait answers a
    // 422 too, though the pieces it rewrote before stand: the server's sentence says so and
    // that repairing again finishes it. The report stays beside it, with its Repair buttons,
    // and the panel adds nothing of its own.
    it('keeps the report and shows the server\'s sentence when a repair stops for another change', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue(postingCountsDrifted(2));
        const stopped = 'Another change to this ledger is still being saved, so the repair stopped. '
            + 'Anything it had already repaired stays repaired; repair again to finish.';
        vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(
            new apiModule.ApiError(422, stopped, 'ledger-write-busy'),
        );
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        await user.click(screen.getByRole('button', { name: /repair posting counts/i }));

        expect(await screen.findByRole('alert')).toHaveTextContent(stopped);
        expect(screen.getByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        expect(screen.getByRole('button', { name: /repair posting counts/i })).toBeInTheDocument();
        expect(screen.queryByRole('status')).toBeNull();
    });

    // A refusal stands beside the report it kept. A check takes that report away at the
    // press, so the refusal goes as the check starts, not when the check answers: while the
    // check runs, it would be an error with nothing beside it to say what it refused.
    it('drops a refused repair\'s error as a check starts', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([]);
        let answerCheck!: () => void;
        vi.spyOn(apiModule, 'checkLedgerConsistency')
            .mockResolvedValueOnce(postingCountsDrifted(2))
            .mockImplementationOnce(() => new Promise((resolve) => {
                answerCheck = () => resolve(postingCountsDrifted(1));
            }));
        const refusal = 'A repair of this ledger is already running. Wait for it to finish before starting another.';
        vi.spyOn(apiModule, 'repairProjection').mockRejectedValue(
            new apiModule.ApiError(422, refusal, 'consistency-repair-in-progress'),
        );
        mockLedger('owner');
        renderPanel();
        const user = userEvent.setup();

        const checkButton = await screen.findByRole('button', { name: /check consistency/i });
        await user.click(checkButton);
        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        await user.click(screen.getByRole('button', { name: /repair posting counts/i }));
        expect(await screen.findByRole('alert')).toHaveTextContent(refusal);

        // Checking… is drawn only while the check is in flight, and the check's answer is
        // held, so every assertion below reads that state.
        await user.click(checkButton);
        await waitFor(() => expect(checkButton).toHaveAccessibleName('Checking…'));
        expect(screen.queryByText(refusal)).toBeNull();
        expect(screen.queryByRole('alert')).toBeNull();
        expect(checkButton).toHaveAccessibleName('Checking…');

        // The report lands while the check still reads as pending, so the settled state is
        // the one where Check consistency reads as itself again: the refusal stays gone.
        answerCheck();
        await waitFor(() => expect(checkButton).toHaveAccessibleName('Check consistency'));
        expect(screen.getByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 1 of 60 disagree');
        expect(screen.queryByText(refusal)).toBeNull();
        expect(screen.queryByRole('alert')).toBeNull();
    });

    /** A report with posting counts out of step, one of the two listed, and a drift event. */
    function driftedWithOneOfTwoListed() {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([driftEvent(null)]);
        vi.spyOn(apiModule, 'checkLedgerConsistency').mockResolvedValue({
            ...clean,
            healthy: false,
            projections: clean.projections.map((p) => p.projection === 'posting_counts'
                ? {
                    ...p, healthy: false, mismatchedCount: 2,
                    mismatches: [{ scope: 'header abc', field: 'header_total_postings', stored: 2, expected: 1, diff: -1 }],
                }
                : p),
        });
    }

    // The server refuses a viewer's repair before it rewrites anything, so a viewer is
    // shown what disagrees and is offered no Repair: no button, no account of what one
    // would do, and no banner saying each figure can be repaired from here.
    it('offers a viewer no repair', async () => {
        driftedWithOneOfTwoListed();
        mockLedger('viewer');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        // The ledger's name is on the screen once the ledger list has answered, so the
        // reader's role is known and the absences below are not a frame before it.
        await waitFor(() => expect(document.querySelector('[data-ledger-name]')).toHaveTextContent('Personal'));
        expect(screen.getByText(/^Showing 1 of 2/).textContent).toBe('Showing 1 of 2.');
        expect(screen.queryByRole('button', { name: /^repair/i })).toBeNull();
        expect(screen.queryByText(/^Repair recounts the postings/)).toBeNull();
        const banner = screen.getByRole('status');
        expect(banner).toHaveTextContent(/trade_prices: 6 of 4000/);
        expect(banner).toHaveTextContent('Run the check to see which figures disagree.');
        expect(banner.textContent).not.toMatch(/repaired from here/);
    });

    // An editor writes, so an editor repairs, as an owner does.
    it('offers an editor the repair', async () => {
        driftedWithOneOfTwoListed();
        mockLedger('editor');
        renderPanel();
        const user = userEvent.setup();

        await user.click(await screen.findByRole('button', { name: /check consistency/i }));

        expect(await screen.findByText(/^Posting counts — /)).toHaveTextContent('Posting counts — 2 of 60 disagree');
        expect(screen.getByText(/^Showing 1 of 2/).textContent).toBe(
            'Showing 1 of 2. Repair fixes all of them, not only the ones listed.',
        );
        expect(screen.getByRole('button', { name: 'Repair posting counts' })).toBeInTheDocument();
        expect(screen.getByText(/^Repair recounts the postings/)).toBeInTheDocument();
    });

    // A person's check records drift as the scheduled one does, so the banner names
    // neither: it says a check reported it.
    it('names no particular check in the drift banner', async () => {
        vi.spyOn(apiModule, 'fetchLedgerEvents').mockResolvedValue([driftEvent(null)]);
        mockLedger('owner');
        renderPanel();

        const banner = await screen.findByRole('status');
        expect(banner).toHaveTextContent(/^A consistency check reported drift on/);
        expect(banner.textContent).not.toMatch(/scheduled/i);
    });
});
