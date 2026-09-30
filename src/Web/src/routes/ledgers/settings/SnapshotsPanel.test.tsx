import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { SnapshotsPanel } from './SnapshotsPanel';
import * as apiModule from '@/lib/api';
import type { SnapshotSummary } from '@/lib/types';

// A snapshot can only be restored on the schema that made it — ADR-0037 Phase 1
// refuses a cross-version restore, so every migration retires every snapshot
// taken before it.
//
// The panel used to offer Restore on EVERY row and let the server refuse after
// the confirm. The row that behaves worst under that is the pre-upgrade
// snapshot: the one taken precisely because an upgrade is risky, which
// announces its uselessness at the moment it is needed.
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
        contentSizeUncompressed: 187_400_000,
        restorable: true,
        ...overrides,
    };
}

function renderPanel() {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
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
