import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import * as apiModule from '@/lib/api';
import type { UseSelectionResult } from '@/lib/useSelection';

import { useRegisterBulkRecovery } from './useRegisterBulkRecovery';

/**
 * The bulk recovery actions both registers share. Each is a precise writer
 * (ADR-0079 as amended 2026-11-03): it refreshes its own register in place and
 * the home screen's reads, and never the register's window key, which would move
 * the reader off their rows.
 */

const LEDGER = 'led-1';
const ACCOUNT = 'acc-1';

function setup() {
    const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
    const selection = { selection: { headerIds: ['h-1'] }, clear: vi.fn() } as unknown as UseSelectionResult;
    const onRefresh = vi.fn();
    const wrapper = ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(
        () => useRegisterBulkRecovery({ ledgerId: LEDGER, accountId: ACCOUNT, selection, onRefresh }),
        { wrapper },
    );
    const keys = () => invalidated.mock.calls.map((c) => c[0]?.queryKey);
    return { result, keys, onRefresh };
}

function expectHomeRefreshedNotRegister(keys: () => unknown[]) {
    expect(keys()).toContainEqual(['home', LEDGER]);
    expect(keys()).toContainEqual(['overview', LEDGER]);
    expect(keys()).toContainEqual(['budget-progress', LEDGER]);
    expect(keys().some((k) => Array.isArray(k) && k[0] === 'register')).toBe(false);
}

describe('useRegisterBulkRecovery', () => {
    beforeEach(() => vi.restoreAllMocks());

    it('a landed unhide refreshes the home screen\'s reads, not the register\'s window', async () => {
        vi.spyOn(apiModule, 'bulkUnhideTransactions').mockResolvedValue({} as never);
        const { result, keys, onRefresh } = setup();

        act(() => result.current.onBulkUnhide());

        await waitFor(() => expect(onRefresh).toHaveBeenCalled());
        expectHomeRefreshedNotRegister(keys);
    });

    it('a landed move refreshes the home screen\'s reads, not the register\'s window', async () => {
        vi.spyOn(apiModule, 'bulkMoveToAccount').mockResolvedValue({} as never);
        const { result, keys, onRefresh } = setup();

        act(() => result.current.onMoveConfirm('acc-2'));

        await waitFor(() => expect(onRefresh).toHaveBeenCalled());
        expectHomeRefreshedNotRegister(keys);
    });
});
