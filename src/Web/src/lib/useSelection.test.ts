import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';

import * as apiModule from '@/lib/api';
import { useSelection } from './useSelection';

afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
});

describe('useSelection', () => {
    it('sends the local date with a select-all, not only the instant', () => {
        // Late evening locally. West of UTC this is already the next day in UTC,
        // which is exactly when an instant and the register's local-date
        // "scheduled" rule disagree.
        vi.useFakeTimers({ toFake: ['Date'] });
        const clicked = new Date(2026, 0, 11, 23, 30);
        vi.setSystemTime(clicked);
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({ count: 0, sumOnAccount: null });

        const { result } = renderHook(() =>
            useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'uncleared' }),
        );
        act(() => result.current.toggleAll());

        const selection = result.current.selection;
        expect(selection.kind).toBe('all');
        if (selection.kind !== 'all') return;
        expect(selection.today).toBe('2026-01-11');
        expect(selection.selectedAt).toBe(clicked.toISOString());
    });

    it('sends no date for an explicit selection, which names its rows', () => {
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({ count: 0, sumOnAccount: null });

        const { result } = renderHook(() =>
            useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'uncleared' }),
        );
        act(() => result.current.setExplicit(['header-1']));

        expect(result.current.selection).toEqual({
            kind: 'explicit',
            accountId: 'account-1',
            headerIds: ['header-1'],
        });
    });
});
