import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { MembersPanel } from './MembersPanel';
import * as apiModule from '@/lib/api';
import type { LedgerMember } from '@/lib/api';
import type { LedgerSummary } from '@/lib/types/ledger';

// Smoke tests for the ledger Members panel (ADR-0083):
//   * an owner gets role pickers + Remove and can change a role;
//   * a non-owner sees the list read-only;
//   * the sole owner's controls are locked (the ≥1-owner guard, mirrored in the UI).

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

const ALICE: LedgerMember = { userId: 'u1', displayName: 'Alice', username: 'alice', role: 'owner' };
const BOB: LedgerMember = { userId: 'u2', displayName: 'Bob', username: 'bob', role: 'editor' };

function renderPanel(members: LedgerMember[], callerRole: string) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    vi.spyOn(apiModule, 'fetchLedgerMembers').mockResolvedValue(members);
    vi.spyOn(apiModule, 'fetchLedgerInvites').mockResolvedValue([]);
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([
        { id: LEDGER_ID, name: 'Books', role: callerRole } as LedgerSummary,
    ]);
    return render(
        <QueryClientProvider client={queryClient}>
            <MembersPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

describe('MembersPanel', () => {
    beforeEach(() => vi.restoreAllMocks());

    it('lets an owner change a member role', async () => {
        const setRole = vi.spyOn(apiModule, 'setLedgerMemberRole').mockResolvedValue(undefined);
        renderPanel([ALICE, BOB], 'owner');

        const select = await screen.findByLabelText('Role for Bob');
        fireEvent.change(select, { target: { value: 'viewer' } });
        await waitFor(() => expect(setRole).toHaveBeenCalledWith(LEDGER_ID, 'u2', 'viewer'));
    });

    it('shows the list read-only to a non-owner', async () => {
        renderPanel([ALICE, BOB], 'viewer');
        expect(await screen.findByText('Alice')).toBeInTheDocument();
        expect(screen.queryByLabelText('Role for Bob')).toBeNull();
        expect(screen.queryByRole('button', { name: 'Remove' })).toBeNull();
    });

    it('locks the sole owner (>=1-owner guard)', async () => {
        renderPanel([ALICE, BOB], 'owner'); // Alice is the only owner
        expect(await screen.findByLabelText('Role for Alice')).toBeDisabled();
        expect(screen.getByLabelText('Role for Bob')).not.toBeDisabled();
    });
});
