import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { problem, type StubbedCall } from '@/lib/annotations/fetchStub';
import { formatLedgerDate } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
import type { RateUnavailable } from '@/lib/types';

import { CATALOG } from './dashboardCatalog';

import {
    account,
    CARD,
    CHECKING,
    customiseWhenRead,
    homeStub,
    layoutWith,
    LEDGER,
    occurrence,
    renderHome,
    schedule,
    series,
    TODAY,
} from './homeTestFixtures';

// The home screen's own rules, through the real page, API client and router, with
// the server mocked at fetch. Every name and figure is invented.

beforeEach(() => {
    // Only the date is faked; timers stay real so queries settle.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 2, 12, 15, 0, 0));
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

const bills = () => screen.findByRole('heading', { level: 2, name: /Bills & income/ }).then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);
// The sentence renders a wide form and a narrow one, one hidden by a container
// query; jsdom applies no CSS, so both are present and the wide one is read.
// The settled sentence is a paragraph; while its reads load it is a placeholder row.
const sentence = () => waitFor(() => {
    const day = screen.getAllByText('Thu, Mar 12', { selector: 'span' }).find((e) => e.parentElement?.tagName === 'P');
    if (!day) throw new Error('the sentence has not settled');
    return day.parentElement as HTMLElement;
});

describe('the default screen', () => {
    it('shows the default widgets in their columns, and no Investments without an investment account', async () => {
        homeStub();
        renderHome();

        expect(await screen.findByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument();
        const titles = await waitFor(() => {
            const h = screen.getAllByRole('heading', { level: 2 }).map((e) => e.textContent ?? '');
            expect(h.some((t) => t.startsWith('Cash flow'))).toBe(true);
            return h;
        });
        expect(titles.map((t) => t.split(' ·')[0]!.trim())).toEqual([
            'Problems', 'Bills & income', 'To review', 'This month', 'Cash flow', 'Feeds & checks', 'Net worth',
        ]);
        expect(screen.queryByRole('heading', { level: 2, name: 'Investments' })).toBeNull();
        expect(screen.queryByRole('heading', { level: 2, name: 'Accounts' })).toBeNull();
    });

    it('says when nothing needs the reader, and what was checked', async () => {
        homeStub();
        renderHome();
        expect(await within(await sentence()).findByText(/Nothing needs you today/)).toBeInTheDocument();
        expect(screen.getAllByText(/consistency not checked yet/).length).toBeGreaterThan(0);
    });
});

describe('Bills & income and the header counts (one rule for both)', () => {
    const later = '2026-03-15';

    it('counts a series with no auto-post and keeps its button', async () => {
        homeStub({ upcoming: [occurrence({ date: later, payee: 'Payee M', autoPost: null })] });
        renderHome();

        const widget = await bills();
        const row = (await within(widget).findByText('Payee M')).closest('li')!;
        expect(within(row).getByRole('button', { name: 'Post or skip…' })).toBeInTheDocument();
        expect(await within(await sentence()).findByRole('button', { name: /1 to post by/ })).toBeInTheDocument();
    });

    it('marks an occurrence the job will post "auto-posts", with no button and no count', async () => {
        homeStub({
            upcoming: [
                occurrence({ date: later, reminderId: 'auto', payee: 'Payee Auto', autoPost: { excludedReason: null } }),
                // The anchor: a manual one in the same list, which IS counted.
                occurrence({ date: later, reminderId: 'manual', payee: 'Payee Manual' }),
            ],
            schedules: { 'reminder-auto-post': schedule({ enabled: true }) },
        });
        renderHome();

        const widget = await bills();
        const auto = (await within(widget).findByText('Payee Auto')).closest('li')!;
        expect(within(auto).getByText('auto-posts')).toBeInTheDocument();
        expect(within(auto).queryByRole('button', { name: 'Post or skip…' })).toBeNull();
        const manual = within(widget).getByText('Payee Manual').closest('li')!;
        expect(within(manual).getByRole('button', { name: 'Post or skip…' })).toBeInTheDocument();
        expect(await within(await sentence()).findByRole('button', { name: /^1 to post by/ })).toBeInTheDocument();
    });

    it('keeps the button and the count when the auto-post schedule is off', async () => {
        homeStub({
            upcoming: [occurrence({ date: later, payee: 'Payee Off', autoPost: { excludedReason: null } })],
            schedules: { 'reminder-auto-post': schedule({ enabled: false }) },
        });
        renderHome();

        const row = (await within(await bills()).findByText('Payee Off')).closest('li')!;
        expect(within(row).getByRole('button', { name: 'Post or skip…' })).toBeInTheDocument();
        expect(within(row).queryByText('auto-posts')).toBeNull();
        expect(await within(await sentence()).findByRole('button', { name: /1 to post by/ })).toBeInTheDocument();
    });

    it('treats a past occurrence of an auto-post series as overdue and offers Post or skip… (ADR-0109 D4)', async () => {
        homeStub({
            upcoming: [
                occurrence({ date: '2026-03-10', payee: 'Payee Late', autoPost: { excludedReason: null } }),
                occurrence({ date: '2026-02-10', payee: 'Payee Late', autoPost: { excludedReason: null } }),
            ],
            schedules: { 'reminder-auto-post': schedule({ enabled: true }) },
        });
        renderHome();

        const widget = await bills();
        const overdue = within(widget).getByRole('region', { name: 'Overdue' });
        const row = (await within(overdue).findByText(/Payee Late/)).closest('li')!;
        expect(within(row).getByText('2 overdue')).toBeInTheDocument();
        expect(within(row).getByRole('button', { name: 'Post or skip…' })).toBeInTheDocument();
        // Per series: one overdue, however many occurrences.
        expect(await within(await sentence()).findByRole('button', { name: '1 overdue' })).toBeInTheDocument();
    });

    it('shows unposted amounts muted, never red', async () => {
        homeStub({ upcoming: [occurrence({ date: later, payee: 'Payee M', amount: -120 })] });
        renderHome();
        const row = (await within(await bills()).findByText('Payee M')).closest('li')!;
        const amount = within(row).getByText('-$120.00');
        expect(amount.className).toContain('text-text-muted');
        expect(amount.className).not.toContain('state-danger');
    });

    it('opens no row menu on a right-click while the layout is edited', async () => {
        const stub = homeStub({ upcoming: [occurrence({ date: later, payee: 'Payee M', autoPost: null })] });
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        renderHome();
        const user = userEvent.setup({ delay: null });
        const rowOf = async () => (await within(await bills()).findByText('Payee M')).closest('li')!;

        // Outside edit mode the right-click opens the row's menu.
        fireEvent.contextMenu(await rowOf());
        expect(within(await screen.findByRole('menu')).getByRole('menuitem', { name: 'Post or skip…' })).toBeInTheDocument();
        await user.keyboard('{Escape}');
        await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());

        const customise = await customiseWhenRead();
        await user.click(customise);
        await screen.findByRole('region', { name: 'Hidden widgets' });
        const row = await rowOf();
        await waitFor(() => expect(within(row).getByRole('button', { name: 'Post or skip…' })).toHaveAttribute('tabindex', '-1'));

        fireEvent.contextMenu(row);

        expect(screen.queryByRole('menu')).toBeNull();
        expect(screen.queryByRole('dialog')).toBeNull();
    });

    it('holds a skipped row\'s Undo until the restore lands, then gives focus to the Post or skip… that takes its place', async () => {
        // ADR-0023 §L: a second press sends no second request, and the control
        // that replaces the one holding focus takes it.
        let restored = false;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        const stub = homeStub({ prefs: layoutWith({ bills: { settings: { showPosted: true } } }) });
        stub.on('GET', /\/reminders\/upcoming$/, () => [occurrence({ date: later, payee: 'Payee S', kind: restored ? 'reminder' : 'skipped' })]);
        stub.on('DELETE', /\/reminders\/series-a\/skip$/, { occurrenceDate: later });
        const answer = globalThis.fetch;
        let deletes = 0;
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            if ((init?.method ?? 'GET').toUpperCase() === 'DELETE') {
                deletes += 1;
                await held;
                restored = true;
            }
            return answer(input, init);
        });
        renderHome();
        const user = userEvent.setup({ delay: null });

        const row = (await within(await bills()).findByText('Payee S')).closest('li')!;
        const undo = within(row).getByRole('button', { name: 'Undo' });
        undo.focus();
        await user.keyboard('{Enter}');
        await user.keyboard('{Enter}');
        await waitFor(() => expect(undo).toHaveAttribute('aria-busy', 'true'));
        expect(undo).toHaveFocus();
        expect(deletes).toBe(1);

        release();
        const act = await within(row).findByRole('button', { name: 'Post or skip…' });
        await waitFor(() => expect(act).toHaveFocus());
        expect(within(row).queryByRole('button', { name: 'Undo' })).toBeNull();
        expect(deletes).toBe(1);
        expect(stub.callsTo('DELETE', /\/skip$/)).toHaveLength(1);
    });

    it('shows a refused Undo instead of dropping it', async () => {
        const stub = homeStub({
            prefs: layoutWith({ bills: { settings: { showPosted: true } } }),
            upcoming: [occurrence({ date: later, payee: 'Payee S', kind: 'skipped' })],
        });
        stub.on('DELETE', /\/skip$/, problem(500, 'The server could not restore it.'));
        renderHome();
        const user = userEvent.setup({ delay: null });

        const row = (await within(await bills()).findByText('Payee S')).closest('li')!;
        await user.click(within(row).getByRole('button', { name: 'Undo' }));

        expect(await within(row).findByRole('alert')).toHaveTextContent('Could not restore that occurrence.');
    });
});

describe('a viewer', () => {
    it('sees the same counts and no write affordance', async () => {
        homeStub({
            role: 'viewer',
            upcoming: [occurrence({ date: '2026-03-15', payee: 'Payee M' })],
            attention: [{ ledgerId: LEDGER, severity: 'warning', count: 1 }],
            outstanding: [{
                subjectKey: 'feed-sync.', topic: 'scheduler', eventKey: 'feed-sync.failed', severity: 'warning',
                summary: 'Feed sync failed', latestAt: '2026-03-11T06:00:00Z', firstUnresolvedAt: '2026-03-10T06:00:00Z',
            }],
        });
        renderHome();

        expect(await screen.findByText('View only')).toBeInTheDocument();
        const widget = await bills();
        // Anchored on the row: it is there, its button is not.
        const row = (await within(widget).findByText('Payee M')).closest('li')!;
        expect(within(row).queryByRole('button', { name: 'Post or skip…' })).toBeNull();
        expect(within(widget).getByText('View only: an owner or editor posts reminders.')).toBeInTheDocument();
        // Problems are readable, their links read Details.
        expect(await screen.findByText('Feed sync failed')).toBeInTheDocument();
        expect(screen.getByRole('link', { name: 'Details →' })).toBeInTheDocument();
        expect(within(await sentence()).getByRole('button', { name: /1 to post by/ })).toBeInTheDocument();
    });
});

describe('Problems', () => {
    it('lists each outstanding subject with its severity word and the page that fixes it', async () => {
        homeStub({
            attention: [{ ledgerId: LEDGER, severity: 'critical', count: 2 }],
            outstanding: [
                {
                    subjectKey: 'backup.', topic: 'backup', eventKey: 'backup.none', severity: 'critical',
                    summary: 'No backup taken', latestAt: '2026-03-11T00:00:00Z', firstUnresolvedAt: '2026-03-01T00:00:00Z',
                },
                {
                    subjectKey: 'consistency.', topic: 'consistency', eventKey: 'consistency.drift', severity: 'warning',
                    summary: 'Balances disagree', latestAt: '2026-03-11T00:00:00Z', firstUnresolvedAt: '2026-03-09T00:00:00Z',
                },
            ],
        });
        renderHome();

        const heading = await screen.findByRole('heading', { level: 2, name: 'Problems · 2' });
        const panel = heading.closest('[id^="home-widget-"]') as HTMLElement;
        const drift = (await within(panel).findByText('Balances disagree')).closest('li')!;
        expect(within(drift).getByText('Warning').className).toContain('text-state-warning');
        expect(within(drift).getByRole('link', { name: 'Check and repair →' })).toHaveAttribute(
            'href', expect.stringContaining('check=true'));
        const backup = within(panel).getByText('No backup taken').closest('li')!;
        expect(within(backup).getByText('Critical').className).toContain('text-state-danger');
        // Not an admin: the backups page is named, not linked.
        expect(within(backup).queryByRole('link')).toBeNull();
        expect(within(backup).getByText('System → Backups')).toBeInTheDocument();
    });
});

describe('a problem\'s day', () => {
    const zone = process.env.TZ;
    afterEach(() => { process.env.TZ = zone; });

    it('is the reader\'s own: an evening west of UTC is not the next day', async () => {
        process.env.TZ = 'America/Los_Angeles';
        homeStub({
            attention: [{ ledgerId: LEDGER, severity: 'warning', count: 1 }],
            outstanding: [{
                // Evening of the 10th on the coast, the 11th in UTC.
                subjectKey: 'consistency.', topic: 'consistency', eventKey: 'consistency.drift', severity: 'warning',
                summary: 'Balances disagree', latestAt: '2026-03-11T03:28:00Z', firstUnresolvedAt: '2026-03-11T03:28:00Z',
            }],
        });
        renderHome();
        const row = (await screen.findByText('Balances disagree')).closest('li')!;
        expect(row).toHaveTextContent('since Mar 10');
        expect(row.textContent).not.toContain('Mar 11');
    });
});

describe('the layout', () => {
    it('saves a move with the updatedAt it read, and offers theirs or mine when another save came first', async () => {
        const stub = homeStub({ prefs: { widgets: [], updatedAt: '2026-03-01T00:00:00Z' } });
        let attempts = 0;
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => {
            attempts++;
            if (attempts === 1) return problem(422, 'The layout changed since it was read.', 'dashboard-prefs-stale');
            return { ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' };
        });
        renderHome();
        const user = userEvent.setup({ delay: null });

        const customise = await customiseWhenRead();
        await user.click(customise);
        await user.click(await screen.findByRole('button', { name: 'Move Bills & income down' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));

        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        const first = stub.callsTo('PUT', /\/preferences\/dashboard$/)[0]!.body as { updatedAt: string; widgets: { key: string }[] };
        expect(first.updatedAt).toBe('2026-03-01T00:00:00Z');
        const main = first.widgets.filter((w) => ['problems', 'bills', 'review', 'budget-pace', 'cash-flow'].includes(w.key));
        expect(main.map((w) => w.key)).toEqual(['problems', 'review', 'bills', 'budget-pace', 'cash-flow']);

        // Stale: the choice, then "Keep mine" saves the same layout again.
        expect(await screen.findByText('Your layout changed somewhere else')).toBeInTheDocument();
        await user.click(screen.getByRole('button', { name: 'Keep mine' }));
        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(2));
        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
    });

    it('moves a widget to the other column from the phone\'s column choice', async () => {
        const stub = homeStub();
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        renderHome();
        const user = userEvent.setup({ delay: null });

        const customise = await customiseWhenRead();
        await user.click(customise);
        await user.selectOptions(await screen.findByRole('combobox', { name: 'Cash flow: column on wide screens' }), 'side');
        await user.click(screen.getByRole('button', { name: 'Done' }));

        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        const body = stub.callsTo('PUT', /\/preferences\/dashboard$/)[0]!.body as { widgets: { key: string; column: string }[] };
        expect(body.widgets.find((w) => w.key === 'cash-flow')?.column).toBe('side');
    });

    it('hides a widget from its menu at once, outside edit mode', async () => {
        const stub = homeStub();
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        renderHome();
        const user = userEvent.setup({ delay: null });

        await user.click(await screen.findByRole('button', { name: 'Cash flow actions' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Hide' }));

        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        const body = stub.callsTo('PUT', /\/preferences\/dashboard$/)[0]!.body as { widgets: { key: string; visible: boolean }[]; updatedAt: null };
        expect(body.widgets.find((w) => w.key === 'cash-flow')?.visible).toBe(false);
        expect(body.updatedAt).toBeNull();
    });

    it('resets to the default with a DELETE', async () => {
        const stub = homeStub({ prefs: { version: 2, columns: 'single', widgets: [], updatedAt: '2026-03-01T00:00:00Z' } });
        stub.on('DELETE', /\/preferences\/dashboard$/, undefined);
        renderHome();
        const user = userEvent.setup({ delay: null });

        const customise = await customiseWhenRead();
        await user.click(customise);
        await user.click(await screen.findByRole('button', { name: 'Reset to default' }));
        await user.click(await screen.findByRole('button', { name: 'Reset' }));

        await waitFor(() => expect(stub.callsTo('DELETE', /\/preferences\/dashboard$/)).toHaveLength(1));
        expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(0);
    });

    it('refuses Reset to default, Cancel and Escape while Done saves, and ends the edit once it has', async () => {
        // A discard or a reset in the middle of the save cannot take it back: the
        // save would still land, over the reset or under the discard's promise.
        const stub = homeStub();
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        stub.on('DELETE', /\/preferences\/dashboard$/, undefined);
        const answer = globalThis.fetch;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            if ((init?.method ?? 'GET').toUpperCase() === 'PUT') await held;
            return answer(input, init);
        });
        renderHome();
        const user = userEvent.setup({ delay: null });

        const customise = await customiseWhenRead();
        await user.click(customise);
        await user.click(await screen.findByRole('button', { name: 'Move Bills & income down' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));
        const saving = await screen.findByRole('button', { name: 'Saving…' });

        for (const name of ['Reset to default', 'Cancel']) {
            const button = screen.getByRole('button', { name });
            expect(button).toHaveAttribute('aria-busy', 'true');
            await user.click(button);
        }
        saving.focus();
        await user.keyboard('{Escape}');
        expect(screen.queryByRole('dialog')).toBeNull();

        release();
        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1);
        expect(stub.callsTo('DELETE', /\/preferences\/dashboard$/)).toHaveLength(0);
    });

    it('refuses a Columns change, a move, a hide and an add while Done saves, so none is dropped', async () => {
        // The save's success clears the draft: a change made to it meanwhile
        // would vanish with it, unsaved and unannounced (ADR-0023 §L).
        const stub = homeStub();
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        const answer = globalThis.fetch;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            if ((init?.method ?? 'GET').toUpperCase() === 'PUT') await held;
            return answer(input, init);
        });
        renderHome();
        const user = userEvent.setup({ delay: null });

        await user.click(await customiseWhenRead());
        await user.click(await screen.findByRole('button', { name: 'Move Bills & income down' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));
        await screen.findByRole('button', { name: 'Saving…' });

        const columns = screen.getByRole('group', { name: 'Columns' });
        const single = within(columns).getByRole('button', { name: 'Single' });
        // "Cash flow, main column, n of m": where it sits, which a move would change.
        const place = screen.getByRole('group', { name: /^Cash flow, main column/ }).getAttribute('aria-label')!;
        const refused = [
            single,
            screen.getByRole('button', { name: 'Move Cash flow up' }),
            screen.getByRole('button', { name: 'Hide Cash flow' }),
            screen.getByRole('button', { name: 'Add Recent transactions' }),
        ];
        for (const control of refused) {
            expect(control, control.getAttribute('aria-label') ?? control.textContent ?? '').toHaveAttribute('aria-busy', 'true');
            expect(control).not.toBeDisabled();
            await user.click(control);
        }
        // The grip offers no drag while the save is in flight, and looks as
        // the pending move buttons beside it do.
        const grip = screen.getByRole('group', { name: /^Cash flow, main column/ }).querySelector('[draggable]');
        expect(grip).toHaveAttribute('draggable', 'false');
        expect(grip).not.toHaveClass('cursor-grab');
        expect(grip).toHaveClass('cursor-default', 'opacity-50');
        const column = screen.getByRole('combobox', { name: 'Cash flow: column on wide screens' });
        expect(column).toHaveAttribute('aria-busy', 'true');
        fireEvent.change(column, { target: { value: 'side' } });
        fireEvent.keyDown(screen.getByRole('group', { name: /^Cash flow, main column/ }), { key: 'ArrowUp', altKey: true });

        // Nothing changed while the save was in flight.
        expect(single).toHaveAttribute('aria-pressed', 'false');
        expect(screen.getByRole('group', { name: place })).toBeInTheDocument();
        expect(screen.queryByRole('group', { name: /^Recent transactions,/ })).toBeNull();
        expect(column).toHaveValue('main');

        release();
        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1);
        const body = stub.callsTo('PUT', /\/preferences\/dashboard$/)[0]!.body as {
            columns: string; widgets: { key: string; column: string; visible: boolean }[];
        };
        expect(body.columns).not.toBe('single');
        expect(body.widgets.find((w) => w.key === 'cash-flow')).toMatchObject({ column: 'main', visible: true });
    });

    it('holds Customise pending, not disabled, while the stored layout is read', async () => {
        // Customise waits on the read: pending keeps it in the tab order and
        // refuses the press, and a failed read (below) is what disables it.
        const stub = homeStub();
        const answer = globalThis.fetch;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            if (new URL(raw, 'http://localhost').pathname.endsWith('/preferences/dashboard')) await held;
            return answer(input, init);
        });
        renderHome();
        const user = userEvent.setup({ delay: null });

        const customise = await screen.findByRole('button', { name: 'Customise' });
        expect(customise).toBeEnabled();
        expect(customise).toHaveAttribute('aria-busy', 'true');
        expect(customise).toHaveAttribute('aria-disabled', 'true');
        await user.click(customise);
        expect(screen.queryByText('Editing layout')).toBeNull();

        release();
        await waitFor(() => expect(customise).not.toHaveAttribute('aria-busy'));
        await user.click(customise);
        expect(await screen.findByText('Editing layout')).toBeInTheDocument();
        expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(0);
    });

    it('shows the default read-only when the stored layout cannot load, and never writes over it', async () => {
        const stub = homeStub({ prefs: problem(500, 'boom') });
        renderHome();
        const user = userEvent.setup({ delay: null });

        expect(await screen.findByText(/Your Overview layout couldn.t load/)).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Customise' })).toBeDisabled();
        await user.click(await screen.findByRole('button', { name: 'Cash flow actions' }));
        const hide = await screen.findByRole('menuitem', { name: 'Hide' });
        expect(hide).toBeDisabled();
        expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(0);
    });
});

const editAccounts = [
    account({ id: CHECKING, name: 'Account A', accountType: 'bank' }),
    account({ id: 'cat-a', name: 'Category A', accountType: 'category', categoryKind: 'expense' }),
];
type Saved = { widgets: { key: string; visible: boolean; column: string; settings: Record<string, unknown> }[] };

/** The page with a layout that saves, and Customise pressed. */
async function editing() {
    const stub = homeStub({ accounts: editAccounts });
    stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
    renderHome();
    const user = userEvent.setup({ delay: null });
    const customise = await customiseWhenRead();
    await user.click(customise);
    return { stub, user, tray: await screen.findByRole('region', { name: 'Hidden widgets' }) };
}
const saved = (stub: ReturnType<typeof homeStub>) => stub.callsTo('PUT', /\/preferences\/dashboard$/)[0]!.body as Saved;

describe('focus when the edit ends, or a widget is hidden', () => {
    // ADR-0023 §L: a control that removes itself hands focus on, never to the
    // page. Every way out of the edit takes the edit bar away, and Customise
    // takes its place; a widget hidden takes its actions button (or its frame's
    // Hide) with it, and the same control on the next widget takes focus.
    const PUT = /\/preferences\/dashboard$/;
    const answerPuts = (stub: ReturnType<typeof homeStub>) =>
        stub.on('PUT', PUT, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
    const customiseButton = () => screen.getByRole('button', { name: 'Customise' });

    async function startEditing() {
        renderHome();
        const user = userEvent.setup({ delay: null });
        await user.click(await customiseWhenRead());
        await screen.findByText('Editing layout');
        return user;
    }

    it('gives focus to Customise after Done with nothing changed', async () => {
        answerPuts(homeStub());
        const user = await startEditing();

        await user.click(screen.getByRole('button', { name: 'Done' }));

        await waitFor(() => expect(customiseButton()).toHaveFocus());
    });

    it('gives focus to Customise once Done\'s save has landed', async () => {
        const stub = answerPuts(homeStub());
        const user = await startEditing();

        await user.click(screen.getByRole('button', { name: 'Move Bills & income down' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));

        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        expect(stub.callsTo('PUT', PUT)).toHaveLength(1);
        await waitFor(() => expect(customiseButton()).toHaveFocus());
    });

    it('gives focus to Customise after Escape with nothing changed', async () => {
        answerPuts(homeStub());
        const user = await startEditing();

        await user.keyboard('{Escape}');

        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        await waitFor(() => expect(customiseButton()).toHaveFocus());
    });

    it('gives focus to Customise after Cancel, then Discard', async () => {
        answerPuts(homeStub());
        const user = await startEditing();

        await user.click(screen.getByRole('button', { name: 'Move Bills & income down' }));
        await user.click(screen.getByRole('button', { name: 'Cancel' }));
        await user.click(await screen.findByRole('button', { name: 'Discard' }));

        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        await waitFor(() => expect(customiseButton()).toHaveFocus());
    });

    it('gives focus to Customise after Reset to default, confirmed', async () => {
        const stub = answerPuts(homeStub());
        stub.on('DELETE', PUT, undefined);
        const user = await startEditing();

        await user.click(screen.getByRole('button', { name: 'Reset to default' }));
        await user.click(await screen.findByRole('button', { name: 'Reset' }));

        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        await waitFor(() => expect(customiseButton()).toHaveFocus());
    });

    it('leaves focus where the user moved it while Done was saving', async () => {
        const stub = answerPuts(homeStub());
        const answer = globalThis.fetch;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            if ((init?.method ?? 'GET').toUpperCase() === 'PUT') await held;
            return answer(input, init);
        });
        const user = await startEditing();

        await user.click(screen.getByRole('button', { name: 'Move Bills & income down' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));
        await screen.findByRole('button', { name: 'Saving…' });
        // A control the edit's end does not take away.
        const elsewhere = document.createElement('button');
        elsewhere.textContent = 'Elsewhere';
        document.body.appendChild(elsewhere);
        elsewhere.focus();
        release();

        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        expect(stub.callsTo('PUT', PUT)).toHaveLength(1);
        expect(elsewhere).toHaveFocus();
        elsewhere.remove();
    });

    it('gives focus to the edit bar\'s first control when Customise starts the edit', async () => {
        // Customise goes as the edit starts; the edit bar takes its place.
        answerPuts(homeStub());
        renderHome();
        const user = userEvent.setup({ delay: null });

        const customise = await customiseWhenRead();
        customise.focus();
        await user.keyboard('{Enter}');

        const columns = await screen.findByRole('group', { name: 'Columns' });
        await waitFor(() => expect(within(columns).getAllByRole('button')[0]).toHaveFocus());
    });

    it('gives focus to the next widget\'s actions when one is hidden from its menu', async () => {
        const stub = answerPuts(homeStub());
        renderHome();
        const user = userEvent.setup({ delay: null });

        const actions = await screen.findByRole('button', { name: 'Cash flow actions' });
        actions.focus();
        await user.keyboard('{Enter}');
        await user.click(await screen.findByRole('menuitem', { name: 'Hide' }));

        await waitFor(() => expect(screen.queryByRole('button', { name: 'Cash flow actions' })).toBeNull());
        expect(stub.callsTo('PUT', PUT)).toHaveLength(1);
        // Cash flow is the main column's last widget; the side column's first comes next.
        expect(screen.getByRole('button', { name: 'Feeds & checks actions' })).toHaveFocus();
    });

    it('gives focus to Customise when the last widget shown is hidden from its menu', async () => {
        const only = Object.fromEntries(CATALOG.map((e) => [e.key, { visible: e.key === 'cash-flow' }]));
        answerPuts(homeStub({ prefs: layoutWith(only) }));
        renderHome();
        const user = userEvent.setup({ delay: null });

        const actions = await screen.findByRole('button', { name: 'Cash flow actions' });
        actions.focus();
        await user.keyboard('{Enter}');
        await user.click(await screen.findByRole('menuitem', { name: 'Hide' }));

        await waitFor(() => expect(screen.queryByRole('button', { name: 'Cash flow actions' })).toBeNull());
        expect(customiseButton()).toHaveFocus();
    });

    it('gives focus to the next frame\'s Hide when one is hidden in the edit, and Escape still ends it', async () => {
        answerPuts(homeStub());
        const user = await startEditing();

        await user.click(screen.getByRole('button', { name: 'Hide Cash flow' }));

        expect(screen.queryByRole('button', { name: 'Hide Cash flow' })).toBeNull();
        expect(screen.getByRole('button', { name: 'Hide Feeds & checks' })).toHaveFocus();
        await user.keyboard('{Escape}');
        expect(await screen.findByRole('button', { name: 'Discard' })).toBeInTheDocument();
    });
});

describe('adding a widget from the tray', () => {
    it('adds a widget at once, at the end of its column, with focus on its frame', async () => {
        const { user, tray } = await editing();
        await user.click(within(tray).getByRole('button', { name: 'Add Accounts' }));

        const frame = await screen.findByRole('group', { name: /^Accounts, side, \d+ of \d+$/ });
        await waitFor(() => expect(frame).toHaveFocus());
        expect(screen.queryByRole('group', { name: 'Accounts settings' })).toBeNull();
    });

    it('opens a widget that needs a subject with its settings, focus in them, and takes it back on Cancel', async () => {
        const { stub, user, tray } = await editing();
        await user.click(within(tray).getByRole('button', { name: 'Add Watchlist' }));

        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });
        await waitFor(() => expect(settings).toHaveFocus());
        expect(screen.getByRole('group', { name: /^Watchlist, side, \d+ of \d+$/ })).toBeInTheDocument();

        await user.click(within(settings).getByRole('button', { name: 'Cancel' }));
        expect(await within(tray).findByRole('button', { name: 'Add Watchlist' })).toBeInTheDocument();
        expect(screen.queryByRole('group', { name: /^Watchlist, side/ })).toBeNull();

        // Nothing changed, so Done has nothing to save.
        await user.click(screen.getByRole('button', { name: 'Done' }));
        await waitFor(() => expect(screen.queryByText('Editing layout')).toBeNull());
        expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(0);
    });

    it('keeps it once its settings are applied', async () => {
        const { stub, user, tray } = await editing();
        await user.click(within(tray).getByRole('button', { name: 'Add Watchlist' }));
        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });

        await user.click(within(settings).getByRole('combobox', { name: 'Add a category' }));
        fireEvent.mouseDown(await screen.findByRole('option', { name: 'Category A' }));
        await user.click(within(settings).getByRole('button', { name: 'Apply' }));
        expect(screen.queryByRole('group', { name: 'Watchlist settings' })).toBeNull();
        await user.click(screen.getByRole('button', { name: 'Done' }));

        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        const watchlist = saved(stub).widgets.find((w) => w.key === 'watchlist')!;
        expect(watchlist).toMatchObject({ visible: true, column: 'side', settings: { categoryIds: ['cat-a'] } });
    });

    it('takes back an add whose settings were never applied when Done is pressed, and keeps the rest', async () => {
        const { stub, user, tray } = await editing();
        await user.click(await screen.findByRole('button', { name: 'Move Bills & income down' }));
        await user.click(within(tray).getByRole('button', { name: 'Add Watchlist' }));
        await screen.findByRole('group', { name: 'Watchlist settings' });
        await user.click(screen.getByRole('button', { name: 'Done' }));

        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        const body = saved(stub);
        expect(body.widgets.find((w) => w.key === 'watchlist')!.visible).toBe(false);
        const main = body.widgets.filter((w) => w.column === 'main' && w.visible).map((w) => w.key);
        expect(main.indexOf('review')).toBeLessThan(main.indexOf('bills'));
    });

    it('treats Escape inside the settings as their Cancel, and the edit goes on', async () => {
        const { user, tray } = await editing();
        await user.click(await screen.findByRole('button', { name: 'Move Bills & income down' }));
        await user.click(within(tray).getByRole('button', { name: 'Add Watchlist' }));
        await waitFor(() => expect(screen.getByRole('group', { name: 'Watchlist settings' })).toHaveFocus());

        await user.keyboard('{Escape}');
        expect(await within(tray).findByRole('button', { name: 'Add Watchlist' })).toBeInTheDocument();
        expect(screen.getByText('Editing layout')).toBeInTheDocument();
        expect(screen.queryByText('Discard your layout changes?')).toBeNull();
    });

    it('closes only the picker\'s list on an Escape there, and still asks on a plain Escape outside a panel', async () => {
        const { user, tray } = await editing();
        await user.click(within(tray).getByRole('button', { name: 'Add Watchlist' }));
        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });

        await user.click(within(settings).getByRole('combobox', { name: 'Add a category' }));
        expect(await screen.findByRole('option', { name: 'Category A' })).toBeInTheDocument();
        await user.keyboard('{Escape}');
        await waitFor(() => expect(screen.queryByRole('option', { name: 'Category A' })).toBeNull());
        expect(screen.getByRole('group', { name: 'Watchlist settings' })).toBeInTheDocument();
        expect(screen.queryByText('Discard your layout changes?')).toBeNull();

        // Outside any panel, Escape is the edit's own Cancel, and the edit has changed.
        screen.getByRole('button', { name: 'Done' }).focus();
        await user.keyboard('{Escape}');
        expect(await screen.findByText('Discard your layout changes?')).toBeInTheDocument();
    });
});

describe('a settings panel', () => {
    it('stays open with what was chosen in it when an added widget moves to the other column', async () => {
        const { stub, user, tray } = await editing();
        await user.click(within(tray).getByRole('button', { name: 'Add Watchlist' }));
        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });
        await user.click(within(settings).getByRole('combobox', { name: 'Add a category' }));
        fireEvent.mouseDown(await screen.findByRole('option', { name: 'Category A' }));
        expect(await within(settings).findByText('1 of 8')).toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: 'Move Watchlist to the main column' }));
        expect(await screen.findByRole('group', { name: /^Watchlist, main column, \d+ of \d+$/ })).toBeInTheDocument();
        const moved = screen.getByRole('group', { name: 'Watchlist settings' });
        expect(within(moved).getByText('1 of 8')).toBeInTheDocument();
        expect(within(moved).getByRole('list', { name: 'Chosen categories' })).toHaveTextContent('Category A');

        await user.click(within(moved).getByRole('button', { name: 'Apply' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));
        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        expect(saved(stub).widgets.find((w) => w.key === 'watchlist'))
            .toMatchObject({ visible: true, column: 'main', settings: { categoryIds: ['cat-a'] } });
    });

    it('stays open with its unapplied change when a shown widget moves to the other column', async () => {
        const { stub, user } = await editing();
        await user.click(await screen.findByRole('button', { name: 'Cash flow settings' }));
        const settings = await screen.findByRole('group', { name: 'Cash flow settings' });
        await user.click(within(settings).getByRole('checkbox', { name: 'Show the table' }));
        expect(within(settings).getByRole('checkbox', { name: 'Show the table' })).not.toBeChecked();

        await user.click(screen.getByRole('button', { name: 'Move Cash flow to the side column' }));
        expect(await screen.findByRole('group', { name: /^Cash flow, side, \d+ of \d+$/ })).toBeInTheDocument();
        const moved = screen.getByRole('group', { name: 'Cash flow settings' });
        expect(within(moved).getByRole('checkbox', { name: 'Show the table' })).not.toBeChecked();

        await user.click(within(moved).getByRole('button', { name: 'Apply' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));
        await waitFor(() => expect(stub.callsTo('PUT', /\/preferences\/dashboard$/)).toHaveLength(1));
        expect(saved(stub).widgets.find((w) => w.key === 'cash-flow'))
            .toMatchObject({ column: 'side', settings: { showTable: false } });
    });

    /** The first option of the panel's first control, Cash flow's months. */
    const firstControl = (settings: HTMLElement) =>
        within(within(settings).getByRole('group', { name: 'Months' })).getAllByRole('button')[0]!;

    it('puts focus on its first control when it opens from the widget\'s menu outside edit mode', async () => {
        homeStub();
        renderHome();
        const user = userEvent.setup({ delay: null });
        await customiseWhenRead();
        await user.click(await screen.findByRole('button', { name: 'Cash flow actions' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Settings…' }));

        const settings = await screen.findByRole('group', { name: 'Cash flow settings' });
        await waitFor(() => expect(firstControl(settings)).toHaveFocus());
    });

    it('puts focus on its first control when it opens from the gear while the layout is edited', async () => {
        const { user } = await editing();
        await user.click(await screen.findByRole('button', { name: 'Cash flow settings' }));

        const settings = await screen.findByRole('group', { name: 'Cash flow settings' });
        await waitFor(() => expect(firstControl(settings)).toHaveFocus());
    });

    it('puts focus on the panel itself when its first control is a picker, so its list stays shut', async () => {
        homeStub({ accounts: editAccounts, prefs: layoutWith({ watchlist: { visible: true, settings: { categoryIds: [] } } }) });
        renderHome();
        const user = userEvent.setup({ delay: null });
        await customiseWhenRead();
        await user.click(await screen.findByRole('button', { name: 'Watchlist actions' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Settings…' }));

        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });
        await waitFor(() => expect(settings).toHaveFocus());
        expect(within(settings).getByRole('combobox', { name: 'Add a category' })).not.toHaveFocus();
        expect(screen.queryByRole('option', { name: 'Category A' })).toBeNull();
    });

    /** Close a settings panel by its Cancel, its Apply or Save, or an Escape inside it. */
    async function close(user: ReturnType<typeof userEvent.setup>, settings: HTMLElement, how: string) {
        if (how === 'Escape') {
            within(settings).getByRole('button', { name: 'Cancel' }).focus();
            await user.keyboard('{Escape}');
        } else {
            await user.click(within(settings).getByRole('button', { name: how }));
        }
    }

    async function addWatchlist() {
        const { user, tray } = await editing();
        await user.click(within(tray).getByRole('button', { name: 'Add Watchlist' }));
        const settings = await screen.findByRole('group', { name: 'Watchlist settings' });
        await waitFor(() => expect(settings).toHaveFocus());
        return { user, tray, settings };
    }

    it.each(['Cancel', 'Escape'])('puts focus on the tray\'s Add when %s takes an add back, so the edit\'s Escape still works', async (how) => {
        const { user, tray, settings } = await addWatchlist();
        await close(user, settings, how);

        await waitFor(() => expect(within(tray).getByRole('button', { name: 'Add Watchlist' })).toHaveFocus());
        // Focus is inside the page, so its Escape ends the edit, which has nothing to discard.
        await user.keyboard('{Escape}');
        expect(await screen.findByRole('button', { name: 'Customise' })).toBeInTheDocument();
    });

    it('puts focus on the widget\'s frame when Apply keeps an add', async () => {
        const { user, settings } = await addWatchlist();
        await user.click(within(settings).getByRole('combobox', { name: 'Add a category' }));
        fireEvent.mouseDown(await screen.findByRole('option', { name: 'Category A' }));
        await close(user, settings, 'Apply');

        await waitFor(() => expect(screen.getByRole('group', { name: /^Watchlist, side, \d+ of \d+$/ })).toHaveFocus());
    });

    it.each(['Cancel', 'Apply', 'Escape'])('puts focus on the widget\'s frame on %s while the layout is edited', async (how) => {
        const { user } = await editing();
        await user.click(await screen.findByRole('button', { name: 'Cash flow settings' }));
        await close(user, await screen.findByRole('group', { name: 'Cash flow settings' }), how);

        await waitFor(() => expect(screen.getByRole('group', { name: /^Cash flow, main column, \d+ of \d+$/ })).toHaveFocus());
        expect(screen.queryByRole('group', { name: 'Cash flow settings' })).toBeNull();
    });

    it.each(['Cancel', 'Save', 'Escape'])('puts focus on the widget\'s actions on %s outside edit mode', async (how) => {
        const stub = homeStub();
        stub.on('PUT', /\/preferences\/dashboard$/, (call: StubbedCall) => ({ ...(call.body as object), updatedAt: '2026-03-12T15:00:00Z' }));
        renderHome();
        const user = userEvent.setup({ delay: null });
        await customiseWhenRead();
        await user.click(await screen.findByRole('button', { name: 'Cash flow actions' }));
        await user.click(await screen.findByRole('menuitem', { name: 'Settings…' }));
        await close(user, await screen.findByRole('group', { name: 'Cash flow settings' }), how);

        await waitFor(() => expect(screen.getByRole('button', { name: 'Cash flow actions' })).toHaveFocus());
        expect(screen.queryByRole('group', { name: 'Cash flow settings' })).toBeNull();
    });

    it('closes with its widget when the widget is hidden, so adding it back opens it closed', async () => {
        const { user } = await editing();
        await user.click(await screen.findByRole('button', { name: 'Cash flow settings' }));
        await screen.findByRole('group', { name: 'Cash flow settings' });
        await user.click(screen.getByRole('button', { name: 'Hide Cash flow' }));

        const tray = await screen.findByRole('region', { name: 'Hidden widgets' });
        await user.click(within(tray).getByRole('button', { name: 'Add Cash flow' }));
        expect(await screen.findByRole('group', { name: /^Cash flow, main column, \d+ of \d+$/ })).toBeInTheDocument();
        expect(screen.queryByRole('group', { name: 'Cash flow settings' })).toBeNull();
    });
});

describe('figures', () => {
    it('reads a total that needs a missing rate "no rate", naming the pair and the day (ADR-0114 D9)', async () => {
        const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-13', reason: 'rate_unavailable' as const };
        homeStub({
            overview: {
                netWorth: null, totalAssets: null, rateUnavailable: [missing],
                comparison: { since: '2026-02-28', netWorth: { then: 900, change: null }, noHistory: false, rateUnavailable: [] },
            },
        });
        renderHome();
        const heading = await screen.findByRole('heading', { level: 2, name: 'Net worth' });
        const panel = heading.closest('[id^="home-widget-"]') as HTMLElement;
        const marked = await within(panel).findAllByText('no rate');
        // Net worth, its change and the assets; the liabilities still print.
        expect(marked).toHaveLength(3);
        expect(marked[0]).toHaveAttribute('title', 'No CAD to USD rate for Mar 13, 2026.');
        expect(within(panel).getByText(formatCurrency(-200, 'USD'))).toBeInTheDocument();
    });

    it('says how much of this month\'s spending is scheduled, and when the projection passes the mark', async () => {
        homeStub({
            budget: {
                actualTotal: 1_660, scheduledTotal: 1_520, elapsedDays: 12, daysInMonth: 31,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 1_660, typical: 300, target: null, targetCurrencyCode: null, mark: 300 }],
                dailyActual: [
                    { categoryId: 'cat-a', day: 2, amount: 140 },
                    { categoryId: 'cat-a', day: 28, amount: 1_520 },
                ],
            },
        });
        renderHome();
        // The phrase carries its trailing separator, held to it by a no-break space.
        expect(await screen.findByText((_, el) => el?.tagName === 'SPAN' && (el.textContent ?? '').startsWith('incl. $1,520.00 scheduled'))).toBeInTheDocument();
        // With no history, spent to date is under the mark until the scheduled bill lands.
        expect(screen.getByText('projected past the mark on Mar 28')).toBeInTheDocument();
    });

    // A payment on the 2nd that the trailing window paid too, and an amount the
    // window usually spends on a later day: from the band's baseline the month
    // projects to exactly PROJECTED, reached on LATER_DAY. The mark is set on it,
    // or a minor unit short of it.
    const PAID = 120;
    const LATER_DAY = 25;
    const LATER = 150;
    const PROJECTED = PAID + LATER;
    const landing = (mark: number) => homeStub({
        budget: {
            actualTotal: PAID, elapsedDays: 12, daysInMonth: 31,
            rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: PAID, typical: mark, target: null, targetCurrencyCode: null, mark }],
            dailyActual: [{ categoryId: 'cat-a', day: 2, amount: PAID }],
            dailyNormal: [
                { categoryId: 'cat-a', day: 2, amount: PAID },
                { categoryId: 'cat-a', day: LATER_DAY, amount: LATER },
            ],
        },
    });
    const thisMonth = () => screen.findByRole('heading', { level: 2, name: /^This month/ })
        .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);

    it('names no crossing for a projection landing exactly on the mark', async () => {
        landing(PROJECTED);
        renderHome();
        const panel = await thisMonth();
        expect(await within(panel).findByRole('img', {
            name: /if the rest of the month goes as usual, projected to finish under the mark/,
        })).toBeInTheDocument();
        expect(within(panel).queryByText(/past the mark/)).toBeNull();
    });

    it('names the day for a projection one minor unit past the mark', async () => {
        landing(PROJECTED - 0.01);
        renderHome();
        const panel = await thisMonth();
        expect(await within(panel).findByText(`projected past the mark on Mar ${LATER_DAY}`)).toBeInTheDocument();
        expect(within(panel).getByRole('img', {
            name: new RegExp(`if the rest of the month goes as usual, projected past the mark on Mar ${LATER_DAY}`),
        })).toBeInTheDocument();
    });

    describe('what a young ledger\'s typical averages', () => {
        const youngLedger = (historyFrom: string | null) => homeStub({
            budget: {
                actualTotal: PAID, elapsedDays: 12, daysInMonth: 31, historyFrom,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: PAID, typical: PROJECTED, target: null, targetCurrencyCode: null, mark: PROJECTED }],
                dailyActual: [{ categoryId: 'cat-a', day: 2, amount: PAID }],
            },
        });

        it('is the whole window for a ledger at least as old as it', async () => {
            youngLedger(null);
            renderHome();
            expect(await within(await thisMonth()).findByText('typical, 3 months')).toBeInTheDocument();
        });

        it('is the whole months since a ledger began on a month\'s first day', async () => {
            youngLedger('2026-01-01');
            renderHome();
            const panel = await thisMonth();
            expect(await within(panel).findByText('typical, 2 months')).toBeInTheDocument();
            expect(within(panel).queryByText('typical, 3 months')).toBeNull();
        });

        it('names the day a ledger began mid-month', async () => {
            const FROM = '2026-01-15';
            youngLedger(FROM);
            renderHome();
            const panel = await thisMonth();
            expect(await within(panel).findByText(`typical, since ${formatLedgerDate(FROM)}`)).toBeInTheDocument();
            expect(within(panel).queryByText(/typical, \d+ month/)).toBeNull();
        });
    });

    describe('a month whose path passes the mark and comes back under it', () => {
        // No history, spent so far on the 2nd, and rows scheduled after today: a bill
        // that takes the month past the mark, then a refund that brings it back under.
        const TODAY = 12;
        const SPENT = 400;
        const BILL = 300;
        const BILL_DAY = 15;
        const REFUND = 200;
        const REFUND_DAY = 20;
        const PAST_DAY = 3;
        const MARK = SPENT + BILL - REFUND / 2;
        const c = (day: number, amount: number, categoryId = 'cat-a') => ({ categoryId, day, amount });
        const month = (cells: ReturnType<typeof c>[]) => {
            const total = cells.reduce((sum, x) => sum + x.amount, 0);
            homeStub({
                budget: {
                    actualTotal: total, elapsedDays: TODAY, daysInMonth: 31,
                    scheduledTotal: cells.filter((x) => x.day > TODAY).reduce((sum, x) => sum + x.amount, 0),
                    rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: total, typical: MARK, target: null, targetCurrencyCode: null, mark: MARK }],
                    dailyActual: cells,
                },
            });
        };
        const underAtTheEnd = /if the rest of the month goes as usual, projected to finish under the mark$/;

        it('names no crossing when the projection passes the mark and ends under it', async () => {
            month([c(2, SPENT), c(BILL_DAY, BILL), c(REFUND_DAY, -REFUND, 'cat-b')]);
            renderHome();
            const panel = await thisMonth();
            expect(await within(panel).findByRole('img', { name: underAtTheEnd })).toBeInTheDocument();
            expect(within(panel).queryByText(/past the mark/)).toBeNull();
        });

        it('names no crossing for a breach already gone that a scheduled refund undoes', async () => {
            month([c(2, SPENT), c(PAST_DAY, MARK - SPENT + REFUND / 4), c(REFUND_DAY, -REFUND, 'cat-b')]);
            renderHome();
            const panel = await thisMonth();
            expect(await within(panel).findByRole('img', { name: underAtTheEnd })).toBeInTheDocument();
            expect(within(panel).queryByText(/past the mark/)).toBeNull();
        });

        it('names the day already gone for a breach that stays past', async () => {
            month([c(2, SPENT), c(PAST_DAY, MARK), c(REFUND_DAY, -REFUND, 'cat-b')]);
            renderHome();
            const panel = await thisMonth();
            expect(await within(panel).findByText(`past the mark since Mar ${PAST_DAY}`)).toBeInTheDocument();
            expect(within(panel).getByRole('img', { name: new RegExp(`; past the mark since Mar ${PAST_DAY}$`) })).toBeInTheDocument();
        });

        it('names the day spending went past a mark of zero, never a finish under it', async () => {
            // The only category is targeted at zero, so the month's mark is zero.
            const ZERO = 0;
            homeStub({
                budget: {
                    actualTotal: SPENT, elapsedDays: TODAY, daysInMonth: 31,
                    rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: SPENT, typical: null, target: ZERO, targetCurrencyCode: 'USD', mark: ZERO }],
                    dailyActual: [c(PAST_DAY, SPENT)],
                },
            });
            renderHome();
            const panel = await thisMonth();
            expect(await within(panel).findByText(`past the mark since Mar ${PAST_DAY}`)).toBeInTheDocument();
            expect(within(panel).queryByRole('img', { name: /finish under the mark/ })).toBeNull();
        });
    });

    it('states no projection on the month\'s last day, with none of the month left', async () => {
        vi.setSystemTime(new Date(2026, 2, 31, 15, 0, 0));
        const DAYS = 31;
        homeStub({
            budget: {
                actualTotal: PAID, elapsedDays: DAYS, daysInMonth: DAYS,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: PAID, typical: PROJECTED, target: null, targetCurrencyCode: null, mark: PROJECTED }],
                dailyActual: [{ categoryId: 'cat-a', day: 2, amount: PAID }],
                dailyNormal: [
                    { categoryId: 'cat-a', day: 2, amount: PAID },
                    { categoryId: 'cat-a', day: LATER_DAY, amount: LATER },
                ],
            },
        });
        renderHome();
        const panel = await thisMonth();
        const chart = await within(panel).findByRole('img', { name: new RegExp(`by day ${DAYS};`) });
        expect(chart).toHaveAccessibleName(/; finished under the mark$/);
        expect(chart).not.toHaveAccessibleName(/if the rest of the month goes as usual/);
    });

    it('counts what the window usually spends on days a short month lacks, on its last day', async () => {
        // A 30-day month after longer ones: the window paid PAID on the 2nd and
        // LATER on the 31st, and the mark sits a minor unit short of both.
        vi.setSystemTime(new Date(2026, 3, 12, 15, 0, 0));
        const DAYS = 30;
        const mark = PAID + LATER - 0.01;
        homeStub({
            budget: {
                month: '2026-04', actualTotal: PAID, elapsedDays: 12, daysInMonth: DAYS,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: PAID, typical: mark, target: null, targetCurrencyCode: null, mark }],
                dailyActual: [{ categoryId: 'cat-a', day: 2, amount: PAID }],
                dailyNormal: [
                    { categoryId: 'cat-a', day: 2, amount: PAID },
                    { categoryId: 'cat-a', day: 31, amount: LATER },
                ],
            },
        });
        renderHome();
        const panel = await thisMonth();
        // The curve's summary settles the panel.
        expect(await within(panel).findByRole('img', { name: /by day 12;/ })).toBeInTheDocument();
        expect(within(panel).getByText(`projected past the mark on Apr ${DAYS}`)).toBeInTheDocument();
    });

    it('lists a category over its mark by less than a whole unit among the rows over: over is over', async () => {
        homeStub({
            budget: {
                actualTotal: 100.4, elapsedDays: 12, daysInMonth: 31,
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: 100.4, typical: 100, target: null, targetCurrencyCode: null, mark: 100 }],
                dailyActual: [{ categoryId: 'cat-a', day: 2, amount: 100.4 }],
            },
        });
        renderHome();
        const heading = await screen.findByRole('heading', { level: 2, name: /^This month/ });
        const panel = heading.closest('[id^="home-widget-"]') as HTMLElement;
        const over = (await within(panel).findByText('Over the mark')).parentElement as HTMLElement;
        expect(within(over).getByRole('link', { name: /Category A/ })).toHaveTextContent('+$0.40 over');
    });

    it('draws no curve and names no crossing date for a month with a missing rate', async () => {
        const missing = { fromCurrency: 'CAD', toCurrency: 'USD', date: '2026-03-02', reason: 'rate_unavailable' as const };
        homeStub({
            budget: {
                actualTotal: null, scheduledTotal: 1_520, elapsedDays: 12, daysInMonth: 31, rateUnavailable: [missing],
                rows: [{ categoryId: 'cat-a', name: 'Category A', parentId: null, actual: null, typical: 300, target: null, targetCurrencyCode: null, mark: 300, rateUnavailable: [missing] }],
                dailyActual: [
                    { categoryId: 'cat-a', day: 2, amount: null, rateUnavailable: [missing] },
                    { categoryId: 'cat-a', day: 28, amount: 1_520 },
                ],
            },
        });
        renderHome();
        const heading = await screen.findByRole('heading', { level: 2, name: /^This month/ });
        const panel = heading.closest('[id^="home-widget-"]') as HTMLElement;
        // The line in place of the curve settles the panel.
        expect(await within(panel).findByText(/^No CAD to USD rate for Mar 2, 2026/)).toBeInTheDocument();
        expect(within(panel).getAllByText('no rate')[0]).toHaveAttribute('title', 'No CAD to USD rate for Mar 2, 2026.');
        expect(within(panel).queryByRole('img')).toBeNull();
        expect(within(panel).queryByText(/past the mark/)).toBeNull();
    });

    it('counts rows to review on real accounts only and links each to its register\'s review view', async () => {
        homeStub({
            accounts: [
                account({ id: CHECKING, name: 'Account A', accountType: 'bank', needsReviewCount: 2 }),
                account({ id: CARD, name: 'Card A', accountType: 'credit_card', needsReviewCount: 4 }),
                account({ id: 'cat-unc', name: 'Uncategorized', accountType: 'category', needsReviewCount: 6 }),
            ],
        });
        renderHome();
        const heading = await screen.findByRole('heading', { level: 2, name: /To review/ });
        expect(heading).toHaveTextContent('6 in 2 accounts');
        const panel = heading.closest('[id^="home-widget-"]') as HTMLElement;
        // The chip line under the head; the list's footer links the same views.
        const chips = within(panel).getByRole('list', { name: 'Rows to review by account' });
        expect(within(chips).getByRole('link', { name: /Card A/ })).toHaveAttribute(
            'href', `/ledgers/${LEDGER}/accounts/${CARD}?status=needs_review`);
        expect(within(await sentence()).getByRole('button', { name: '6 to review' })).toBeInTheDocument();
    });
});

describe('a series overdue before the window', () => {
    it('still counts, from its next-due date', async () => {
        homeStub({ reminders: [series({ id: 'old', payee: 'Payee Old', nextDueDate: '2025-10-01' })] });
        renderHome();
        const row = (await within(await bills()).findByText(/Payee Old/)).closest('li')!;
        expect(within(row).getByText(/overdue since/)).toBeInTheDocument();
        expect(within(await sentence()).getByRole('button', { name: '1 overdue' })).toBeInTheDocument();
        expect(TODAY).toBe('2026-03-12');
    });

    it('prints its amount in the series\' own currency, which no occurrence in the window carries', async () => {
        homeStub({ reminders: [series({ id: 'old', payee: 'Payee Old', nextDueDate: '2025-10-01', amount: -45, currencyCode: 'EUR' })] });
        renderHome();
        const row = (await within(await bills()).findByText(/Payee Old/)).closest('li')!;
        expect(within(row).getByText(formatCurrency(-45, 'EUR'))).toBeInTheDocument();
        expect(row.textContent).not.toContain('$');
    });
});

describe('the Accounts widget', () => {
    const accountsWidget = () => screen.findByRole('heading', { level: 2, name: 'Accounts' })
        .then((h) => h.closest('[id^="home-widget-"]') as HTMLElement);
    const groupHead = async (widget: HTMLElement) => (await within(widget).findByText('Banking')).parentElement as HTMLElement;
    const bankGroup = (
        accounts: { id: string; currencyCode: string; isActive: boolean }[],
        group: { subtotal: number | null; rateUnavailable: RateUnavailable[] } = { subtotal: 300, rateUnavailable: [] },
    ) => ({
        accountGroups: [{
            accountType: 'bank', ...group,
            accounts: accounts.map((a, i) => ({ ...a, name: `Account ${i + 1}`, accountType: 'bank', balance: 100, rateUnavailable: [] })),
        }],
    });

    it('prints a subtotal in the reporting currency, converted, and each account in its own', async () => {
        homeStub({
            prefs: layoutWith({ accounts: { visible: true } }),
            overview: bankGroup([{ id: 'a1', currencyCode: 'EUR', isActive: true }, { id: 'a2', currencyCode: 'EUR', isActive: true }]),
        });
        renderHome();
        const widget = await accountsWidget();
        const head = await groupHead(widget);
        expect(within(head).getByText(formatCurrency(300, 'USD'))).toBeInTheDocument();
        expect(within(head).queryByText(formatCurrency(300, 'EUR'))).toBeNull();
        expect(within(widget).getAllByText(formatCurrency(100, 'EUR'))).toHaveLength(2);
    });

    it('reads a subtotal "no rate" when a closed account it counts, hidden from the list, has no rate', async () => {
        const missing: RateUnavailable = { fromCurrency: 'GBP', toCurrency: 'USD', date: '2026-03-13', reason: 'rate_unavailable' };
        homeStub({
            prefs: layoutWith({ accounts: { visible: true } }),
            overview: bankGroup(
                [{ id: 'a1', currencyCode: 'USD', isActive: true }, { id: 'a2', currencyCode: 'GBP', isActive: false }],
                { subtotal: null, rateUnavailable: [missing] },
            ),
        });
        renderHome();
        const widget = await accountsWidget();
        const head = await groupHead(widget);
        // Anchor: the open account is listed and the closed one is not.
        expect(within(widget).getByText('Account 1')).toBeInTheDocument();
        expect(within(widget).queryByText('Account 2')).toBeNull();
        expect(within(head).getByText('no rate')).toHaveAttribute('title', 'No GBP to USD rate for Mar 13, 2026.');
    });
});

describe('Bills & income figures', () => {
    it('prints each occurrence in its source account\'s currency, and none for a series with no source', async () => {
        homeStub({
            upcoming: [
                occurrence({ date: '2026-03-15', reminderId: 'euro', payee: 'Payee Euro', amount: -120, currencyCode: 'EUR' }),
                occurrence({
                    date: '2026-03-15', reminderId: 'custom', payee: 'Payee Custom', amount: 0,
                    sourceAccountId: null, sourceAccountName: null, currencyCode: null,
                    estimate: { requestedSampleCount: 3, availableSampleCount: 0, amount: null, unavailableReason: 'no-source-account' },
                }),
            ],
        });
        renderHome();
        const widget = await bills();
        const euro = (await within(widget).findByText('Payee Euro')).closest('li')!;
        expect(within(euro).getByText(formatCurrency(-120, 'EUR'))).toBeInTheDocument();
        // No source, so no currency: a dash, not a figure in dollars.
        const custom = within(widget).getByText('Payee Custom').closest('li')!;
        expect(within(custom).getAllByText('—').length).toBeGreaterThan(0);
        expect(euro.textContent).not.toContain('$');
        expect(custom.textContent).not.toContain('$');
    });

    it('prints an overdue series in its earliest occurrence\'s currency', async () => {
        homeStub({ upcoming: [occurrence({ date: '2026-03-10', payee: 'Payee Late', amount: -30, currencyCode: 'GBP' })] });
        renderHome();
        const overdue = within(await bills()).getByRole('region', { name: 'Overdue' });
        const row = (await within(overdue).findByText(/Payee Late/)).closest('li')!;
        expect(within(row).getByText(formatCurrency(-30, 'GBP'))).toBeInTheDocument();
        expect(row.textContent).not.toContain('$');
    });
});

// Mig 220: an estimating series' amount is not the bill, whether it is the estimate
// or the amount entered standing in before anything has posted.
const ESTIMATE_MADE = { requestedSampleCount: 3, availableSampleCount: 2, amount: -50, unavailableReason: null };
const ESTIMATE_NOT_YET = { requestedSampleCount: 3, availableSampleCount: 0, amount: null, unavailableReason: 'no-history' };

describe('Bills & income estimates', () => {
    it('marks an estimating series\' amount "≈", with an estimate or before it has one, and a fixed amount not', async () => {
        homeStub({
            upcoming: [
                occurrence({ reminderId: 'made', payee: 'Payee Made', amount: -50, estimate: ESTIMATE_MADE }),
                occurrence({ reminderId: 'not-yet', payee: 'Payee New', amount: -35, estimate: ESTIMATE_NOT_YET }),
                occurrence({ reminderId: 'fixed', payee: 'Payee Fixed', amount: -20 }),
            ],
        });
        renderHome();
        const widget = await bills();
        const row = async (payee: string) => (await within(widget).findByText(payee)).closest('li')!;

        const made = await row('Payee Made');
        expect(within(made).getByText(formatCurrency(-50, 'USD'))).toBeInTheDocument();
        expect(within(made).getByText('≈')).toHaveAttribute('title', 'Estimated from the last 2 occurrences');

        const notYet = await row('Payee New');
        expect(within(notYet).getByText(formatCurrency(-35, 'USD'))).toBeInTheDocument();
        expect(within(notYet).getByText('≈'))
            .toHaveAttribute('title', 'Estimated: the amount entered is used until an estimate can be made');

        const fixed = await row('Payee Fixed');
        expect(within(fixed).getByText(formatCurrency(-20, 'USD'))).toBeInTheDocument();
        expect(fixed.textContent).not.toContain('≈');
    });

    it('marks an overdue estimating series the same way, from its earliest occurrence or, past the window, the series', async () => {
        homeStub({
            upcoming: [
                occurrence({ date: '2026-03-10', reminderId: 'late', payee: 'Payee Late', amount: -35, estimate: ESTIMATE_NOT_YET }),
                occurrence({ date: '2026-03-09', reminderId: 'late-fixed', payee: 'Payee Fixed', amount: -20 }),
            ],
            reminders: [series({ id: 'old', payee: 'Payee Old', nextDueDate: '2025-10-01', amount: -50, estimate: ESTIMATE_MADE })],
        });
        renderHome();
        const overdue = within(await bills()).getByRole('region', { name: 'Overdue' });
        const row = async (payee: RegExp) => (await within(overdue).findByText(payee)).closest('li')!;

        const late = await row(/Payee Late/);
        expect(within(late).getByText(formatCurrency(-35, 'USD'))).toBeInTheDocument();
        expect(within(late).getByText('≈'))
            .toHaveAttribute('title', 'Estimated: the amount entered is used until an estimate can be made');

        const old = await row(/Payee Old/);
        expect(within(old).getByText(formatCurrency(-50, 'USD'))).toBeInTheDocument();
        expect(within(old).getByText('≈')).toHaveAttribute('title', 'Estimated from the last 2 occurrences');

        const fixed = await row(/Payee Fixed/);
        expect(within(fixed).getByText(formatCurrency(-20, 'USD'))).toBeInTheDocument();
        expect(fixed.textContent).not.toContain('≈');
    });
});
