import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { SnapshotsPanel } from './SnapshotsPanel';
import * as apiModule from '@/lib/api';
import type { SnapshotSummary } from '@/lib/types';

// A snapshot can only be restored on the schema that made it — ADR-0037 Phase 1
// refuses a cross-version restore, so every migration retires every snapshot
// taken before it.
//
// Offering Restore on EVERY row and letting the server refuse after the
// confirm would be worst for the pre-upgrade snapshot: the one taken precisely
// because an upgrade is risky, which would announce its uselessness at the
// moment it is needed.
//
// `restorable` is computed server-side from the same comparison the restore
// endpoint makes (see SnapshotsTests.List_says_which_snapshots_are_restorable_*),
// so these tests only pin what the panel DOES with it.

const LEDGER_ID = '00000000-0000-0000-0000-0000000000a1';
const LIVE_SCHEMA = '230_flip_header_overrides_to_originals.sql';
const OLD_SCHEMA = '226_register_scopes_to_many_accounts.sql';

function snap(overrides: Partial<SnapshotSummary> & { id: string }): SnapshotSummary {
    return {
        createdAt: '2026-09-25T03:05:00Z',
        createdByUserId: '00000000-0000-0000-0000-0000000000u1',
        kind: 'auto',
        description: null,
        schemaVersion: LIVE_SCHEMA,
        contentSizeStored: 23_400_000,
        restorable: true,
        ...overrides,
    };
}

type Ledgers = Awaited<ReturnType<typeof apiModule.fetchVisibleLedgers>>;

function renderPanel(role: 'owner' | 'editor' | 'viewer' = 'owner') {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    // The reader's role, from the read the panel asks it of.
    const ledgers = [{ id: LEDGER_ID, name: 'Ledger', role }] as Ledgers;
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
    client.setQueryData(['ledgers'], ledgers);
    return render(
        <QueryClientProvider client={client}>
            <SnapshotsPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

/** The row element for a snapshot, located by its own size+schema line. */
async function rowFor(schemaVersion: string): Promise<HTMLElement> {
    const line = await screen.findByText(new RegExp(`schema ${schemaVersion}`, 'i'));
    return line.closest('li') as HTMLElement;
}

describe('SnapshotsPanel — Restore is offered only where it can work', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        // ScheduleControl renders inside this panel and fetches its own
        // schedule; stub it so no call escapes and the panel settles.
        vi.spyOn(apiModule, 'fetchSchedule').mockResolvedValue({
            enabled: false, timeOfDay: '03:01', timeZone: 'America/New_York',
            nextRunAt: null, lastAttemptAt: null, lastOutcome: null,
        } as never);
    });

    it('shows what a snapshot occupies as stored, and says so', async () => {
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue([
            snap({ id: 's-size', contentSizeStored: 23_400_000 }),
        ]);

        renderPanel();

        // The row shows the fixture's byte count in megabytes, to one decimal. The word "stored" is
        // what keeps the figure from being read as the size of a file, or compared with a backup,
        // which is compressed and leaves snapshots out.
        expect(await screen.findByText(`22.3 MB stored · schema ${LIVE_SCHEMA}`)).toBeInTheDocument();
    });

    it('offers Restore on a snapshot whose schema still matches', async () => {
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue([
            snap({ id: 's-live', schemaVersion: LIVE_SCHEMA, restorable: true }),
        ]);

        renderPanel();

        const row = await rowFor(LIVE_SCHEMA);
        expect(within(row).getByRole('button', { name: /restore/i })).toBeInTheDocument();
        expect(within(row).getByRole('button', { name: /delete/i })).toBeInTheDocument();
    });

    it('omits Restore — and says why — on a snapshot an upgrade retired', async () => {
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue([
            snap({ id: 's-old', schemaVersion: OLD_SCHEMA, restorable: false }),
        ]);

        renderPanel();

        // Anchored on the row's own schema line first, so the absence below is
        // judged against a rendered row rather than a pending frame — and on
        // text unique to a row, not on panel copy that also mentions schemas.
        const row = await rowFor(OLD_SCHEMA);

        expect(within(row).queryByRole('button', { name: /restore/i })).toBeNull();
        expect(within(row).getByText(/retired by an app upgrade/i)).toBeInTheDocument();
        // Delete has to remain: a retired snapshot still occupies one of the
        // five slots, so removing it is the only way to make room.
        expect(within(row).getByRole('button', { name: /delete/i })).toBeInTheDocument();
    });

    it('decides per row, not for the whole list', async () => {
        // The bug this guards against is a panel that reads one snapshot's
        // flag and applies it to all of them.
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue([
            snap({ id: 's-live', schemaVersion: LIVE_SCHEMA, restorable: true }),
            snap({ id: 's-old', schemaVersion: OLD_SCHEMA, restorable: false }),
        ]);

        renderPanel();

        const live = await rowFor(LIVE_SCHEMA);
        const old = await rowFor(OLD_SCHEMA);

        expect(within(live).getByRole('button', { name: /restore/i })).toBeInTheDocument();
        expect(within(old).queryByRole('button', { name: /restore/i })).toBeNull();
    });
});

describe('SnapshotsPanel — a create that brings the ledger to its cap', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchSchedule').mockResolvedValue({
            enabled: false, timeOfDay: '03:01', timeZone: 'America/New_York',
            nextRunAt: null, lastAttemptAt: null, lastOutcome: null,
        } as never);
    });

    // ADR-0023 §L: the fifth snapshot disables '+ Create snapshot', which the
    // dialog gives focus back to as it closes. Focus goes to the new
    // snapshot's row, its first control, never to the page.
    it('gives focus to the new snapshot\'s row once the list it was refetched into is drawn', async () => {
        const four = [1, 2, 3, 4].map((n) => snap({
            id: `s-${n}`, kind: 'manual', createdAt: `2026-09-2${n}T03:05:00Z`, schemaVersion: `${n}_older.sql`,
        }));
        const made = snap({ id: 's-new', kind: 'manual', createdAt: '2026-09-29T03:05:00Z', schemaVersion: '5_newest.sql' });
        vi.spyOn(apiModule, 'fetchSnapshots')
            .mockResolvedValueOnce(four)
            .mockResolvedValue([made, ...four]);
        vi.spyOn(apiModule, 'createSnapshot').mockResolvedValue({ snapshot: made });
        const user = userEvent.setup();

        renderPanel();

        const create = await screen.findByRole('button', { name: '+ Create snapshot' });
        await rowFor('4_older.sql');
        expect(create).toBeEnabled();
        await user.click(create);
        const dialog = await screen.findByRole('dialog', { name: 'Create snapshot' });
        await user.click(within(dialog).getByRole('button', { name: 'Create' }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        const newRow = await rowFor('5_newest.sql');
        expect(within(newRow).getByRole('button', { name: 'Restore' })).toHaveFocus();
        expect(create).toBeDisabled();
    });
});

describe('SnapshotsPanel — a deleted snapshot\'s row', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchSchedule').mockResolvedValue({
            enabled: false, timeOfDay: '03:01', timeZone: 'America/New_York',
            nextRunAt: null, lastAttemptAt: null, lastOutcome: null,
        } as never);
    });

    const three = [1, 2, 3].map((n) => snap({ id: `s-${n}`, schemaVersion: `${n}_kept.sql` }));

    async function deleteRow(schema: string, remaining: SnapshotSummary[]) {
        const user = userEvent.setup();
        const row = await rowFor(schema);
        vi.spyOn(apiModule, 'deleteSnapshot').mockResolvedValue(undefined as never);
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue(remaining);
        await user.click(within(row).getByRole('button', { name: 'Delete' }));
        const confirm = await screen.findByRole('dialog');
        await user.click(within(confirm).getByRole('button', { name: 'Delete' }));
        await waitFor(() => expect(screen.queryByText(new RegExp(`schema ${schema}`))).toBeNull());
    }

    // ADR-0023 §L: the confirm gives focus back to the row's Delete, which the
    // refetched list then takes away. Focus goes to the next row's Delete, else
    // the previous row's, else '+ Create snapshot'.
    it('gives focus to the next row\'s Delete', async () => {
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue(three);
        renderPanel();
        await deleteRow('1_kept.sql', [three[1]!, three[2]!]);
        const next = await rowFor('2_kept.sql');
        await waitFor(() => expect(within(next).getByRole('button', { name: 'Delete' })).toHaveFocus());
    });

    it('gives focus to + Create snapshot when no row is left', async () => {
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue([three[0]!]);
        renderPanel();
        await deleteRow('1_kept.sql', []);
        await waitFor(() => expect(screen.getByRole('button', { name: '+ Create snapshot' })).toHaveFocus());
    });
});

describe('SnapshotsPanel — by role (ADR-0023 §L)', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchSchedule').mockResolvedValue({
            enabled: false, timeOfDay: '03:01', timeZone: 'America/New_York',
            nextRunAt: null, lastAttemptAt: null, lastOutcome: null,
        } as never);
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue([
            snap({ id: 's-live', schemaVersion: LIVE_SCHEMA, restorable: true }),
            snap({ id: 's-old', schemaVersion: OLD_SCHEMA, restorable: false }),
        ]);
    });

    it('gives a viewer the list, with no create, restore, delete or schedule', async () => {
        renderPanel('viewer');
        const live = await rowFor(LIVE_SCHEMA);
        const old = await rowFor(OLD_SCHEMA);

        expect(within(live).queryAllByRole('button')).toEqual([]);
        expect(within(old).queryAllByRole('button')).toEqual([]);
        // A retired one says why it cannot be restored, and offers no delete either.
        expect(old).toHaveTextContent(/it can no longer be restored\.$/);
        expect(screen.queryByRole('button', { name: '+ Create snapshot' })).toBeNull();
        expect(screen.queryByText('Take an automatic snapshot each day')).toBeNull();
        expect(screen.queryByText(/Create one before a bulk edit/)).toBeNull();
    });

    it('gives an editor create, delete and the schedule, and no restore', async () => {
        renderPanel('editor');
        const live = await rowFor(LIVE_SCHEMA);

        expect(within(live).getAllByRole('button').map((b) => b.textContent)).toEqual(['Delete']);
        expect(screen.getByRole('button', { name: '+ Create snapshot' })).toBeInTheDocument();
        expect(screen.getByText('Take an automatic snapshot each day')).toBeInTheDocument();
    });

    it('gives the owner restore beside them', async () => {
        renderPanel('owner');
        const live = await rowFor(LIVE_SCHEMA);
        expect(within(live).getAllByRole('button').map((b) => b.textContent)).toEqual(['Restore', 'Delete']);
    });

    it('tells a viewer of an empty list nothing to take', async () => {
        vi.spyOn(apiModule, 'fetchSnapshots').mockResolvedValue([]);
        renderPanel('viewer');
        expect(await screen.findByText('No snapshots yet.')).toBeInTheDocument();
        expect(screen.queryByText(/to take one/)).toBeNull();
    });
});
