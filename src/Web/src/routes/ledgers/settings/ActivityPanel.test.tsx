import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { ActivityPanel } from './ActivityPanel';
import * as apiModule from '@/lib/api';
import type { LedgerOperationSummary } from '@/lib/types';

// The undo-import entry point.
//
// `undo-import` existed for releases with exactly one caller: the import
// dialog. The undo therefore lived only as long as that dialog stayed open —
// close it, or go and look at what actually landed, and the endpoint was
// unreachable from the app. An import you have not looked at is the one you
// are least likely to want to undo, so the affordance belonged where you go to
// look.
//
// These tests exist because "the button renders" is not the property that
// matters. What matters is that clicking it reaches the endpoint, that the
// confirm states what will be lost, and that it is absent where an undo would
// be a lie.

const LEDGER_ID = '00000000-0000-0000-0000-0000000000b1';

function op(overrides: Partial<LedgerOperationSummary>): LedgerOperationSummary {
    return {
        id: '00000000-0000-0000-0000-00000000f001',
        family: 'ingest',
        providerKey: 'file',
        triggeredVia: 'file-upload',
        status: 'completed',
        startedAt: '2026-09-20T12:00:00Z',
        completedAt: '2026-09-20T12:00:03Z',
        triggeredByUserId: null,
        details: { txns_inserted: 12 },
        errorCount: 0,
        ...overrides,
    };
}

function renderPanel() {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={client}>
            <ActivityPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

describe('ActivityPanel is a log, not a control surface', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('offers no undo — or any other action — on a listed operation', async () => {
        // The panel briefly carried "Undo this import" (added 2026-09-23,
        // removed 2026-09-25). A log reports what ran; putting an irreversible
        // hard-delete of transactions in a list of past events is a category
        // error, and the context is worst there — one summarised line from days
        // ago, versus the import modal where you have just seen what landed.
        //
        // Anchored on "12 new" — the fixture's own txns_inserted summary, which
        // renders ONLY on a row. The first version of this anchored on
        // /file import/i and was worthless: PROVIDER_OPTIONS carries a "File
        // import" filter option, so the anchor matched the dropdown and the
        // absences below were judged against a panel whose rows had not
        // rendered. It passed with an undo button injected.
        vi.spyOn(apiModule, 'fetchLedgerOperations').mockResolvedValue([op({})]);

        renderPanel();

        expect(await screen.findByText(/12 new/i)).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /undo/i })).toBeNull();
        expect(screen.queryByText(/remove \d+ transaction/i)).toBeNull();
    });
});
