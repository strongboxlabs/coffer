import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import * as apiModule from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';

import { canWriteLedger, isLedgerOwner } from './ledgerRole';
import { useLedgerRole } from './useLedgerRole';

// Every control on Structure is drawn or not by this hook, so its edges are pinned here rather
// than inferred from the panels that use it: what a role it has never heard of can do, what the
// answer is while the ledger list is still on its way, and what it is for a ledger that is not
// in the list at all. The rule in each case is the rail's: only an owner or an editor can edit.

const LEDGER = '00000000-0000-0000-0000-000000000010';

function wrapper(queryClient: QueryClient) {
    return function Wrapper({ children }: { children: ReactNode }) {
        return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
    };
}

function client() {
    return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

describe('useLedgerRole', () => {
    it.each([
        ['owner', true, true],
        ['editor', true, false],
        ['viewer', false, false],
        // A role this client has not been taught is not a licence to edit.
        ['auditor', false, false],
    ])('reads %s as canEdit=%s, isOwner=%s', async (role, canEdit, isOwner) => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([{ id: LEDGER, name: 'L', role }] as never);
        const { result } = renderHook(() => useLedgerRole(LEDGER), { wrapper: wrapper(client()) });

        await waitFor(() => expect(result.current.role).toBe(role));
        expect(result.current.canEdit).toBe(canEdit);
        expect(result.current.isOwner).toBe(isOwner);
        expect(result.current.ledger?.id).toBe(LEDGER);
    });

    it('cannot edit while the ledger list is still on its way', () => {
        // A read that never answers, so the frame under test is the pending one and stays it.
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockReturnValue(new Promise(() => {}));
        const { result } = renderHook(() => useLedgerRole(LEDGER), { wrapper: wrapper(client()) });

        expect(result.current.role).toBeUndefined();
        expect(result.current.ledger).toBeUndefined();
        expect(result.current.canEdit).toBe(false);
        expect(result.current.isOwner).toBe(false);
    });

    it('cannot edit a ledger that is not in the list', async () => {
        // An owner of some other ledger, which must not leak into this one's answer.
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(
            [{ id: '00000000-0000-0000-0000-000000000099', name: 'Other', role: 'owner' }] as never,
        );
        const queryClient = client();
        const { result } = renderHook(() => useLedgerRole(LEDGER), { wrapper: wrapper(queryClient) });

        await waitFor(() => expect(queryClient.getQueryState(['ledgers'])?.status).toBe('success'));
        expect(result.current.ledger).toBeUndefined();
        expect(result.current.canEdit).toBe(false);
    });

    // The hook and the predicate are one rule: every screen asks the hook, and the hook answers
    // with the predicate, so the two never disagree on any role, or on none.
    it.each([
        ['owner', 'owner'],
        ['editor', 'editor'],
        ['viewer', 'viewer'],
        ['a role this client was never taught', 'auditor'],
        ['no role (a ledger not in the list)', undefined],
    ])('answers as canWriteLedger and isLedgerOwner do for %s', async (_label, role) => {
        const listed = role === undefined ? [] : [{ id: LEDGER, name: 'L', role }];
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(listed as never);
        const queryClient = client();
        const { result } = renderHook(() => useLedgerRole(LEDGER), { wrapper: wrapper(queryClient) });

        await waitFor(() => expect(queryClient.getQueryState(['ledgers'])?.status).toBe('success'));
        expect(result.current.role).toBe(role);
        expect(result.current.canEdit).toBe(canWriteLedger(role as LedgerSummary['role'] | undefined));
        expect(result.current.isOwner).toBe(isLedgerOwner(role as LedgerSummary['role'] | undefined));
    });

    it('answers for no ledger at all, as the rail asks before one is chosen', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue([{ id: LEDGER, name: 'L', role: 'owner' }] as never);
        const queryClient = client();
        const { result } = renderHook(() => useLedgerRole(undefined), { wrapper: wrapper(queryClient) });

        await waitFor(() => expect(queryClient.getQueryState(['ledgers'])?.status).toBe('success'));
        expect(result.current.ledger).toBeUndefined();
        expect(result.current.canEdit).toBe(false);
    });

    it('cannot edit when the ledger list could not be read', async () => {
        vi.spyOn(apiModule, 'fetchVisibleLedgers').mockRejectedValue(new Error('offline'));
        const queryClient = client();
        const { result } = renderHook(() => useLedgerRole(LEDGER), { wrapper: wrapper(queryClient) });

        await waitFor(() => expect(queryClient.getQueryState(['ledgers'])?.status).toBe('error'));
        expect(result.current.canEdit).toBe(false);
    });
});
