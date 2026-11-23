import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';

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
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({ count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });

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
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({ count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });

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

    it('marks the summary current only for the selection it was resolved for', async () => {
        // A changed selection keeps the previous summary on hand until its own
        // arrives; isSummaryCurrent tells the footer not to read it as this one's.
        const answers: Array<(s: { count: number; totalCount: number; readOnlyCount: number; sumOnAccount: number; currencyCode: string }) => void> = [];
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockImplementation(
            () => new Promise((resolve) => { answers.push(resolve); }),
        );
        const { result } = renderHook(() =>
            useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'all', debounceMs: 0 }),
        );

        act(() => result.current.setExplicit(['header-1']));
        await waitFor(() => expect(answers).toHaveLength(1));
        await act(async () => { answers[0]!({ count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' }); });
        expect(result.current.isSummaryCurrent).toBe(true);

        act(() => result.current.setExplicit(['header-1', 'header-2']));
        expect(result.current.summary?.totalCount).toBe(1);
        expect(result.current.isSummaryCurrent).toBe(false);

        await waitFor(() => expect(answers).toHaveLength(2));
        await act(async () => { answers[1]!({ count: 2, totalCount: 2, readOnlyCount: 1, sumOnAccount: 0, currencyCode: 'USD' }); });
        expect(result.current.isSummaryCurrent).toBe(true);
        expect(result.current.summary?.readOnlyCount).toBe(1);
    });

    it('sends a widened register\'s scope with an explicit selection', () => {
        // The server judges which checked rows the register can change in the
        // register's own scope, a category's descendants included.
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({ count: 0, totalCount: 0, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });
        const filter = { includeSubcategories: true };

        const { result } = renderHook(() =>
            useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'all', filter }),
        );
        act(() => result.current.setExplicit(['header-1']));

        expect(result.current.selection).toEqual({
            kind: 'explicit',
            accountId: 'account-1',
            headerIds: ['header-1'],
            includeSubcategories: true,
        });
    });
});

describe('useSelection and Escape', () => {
    // Escape on the page clears the selection. An Escape something nearer
    // already used is not the page's: the inline editor cancels on one from
    // any of its controls, buttons included, and marks it with preventDefault.
    function selectOne() {
        vi.spyOn(apiModule, 'fetchSelectionSummary').mockResolvedValue({ count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });
        const hook = renderHook(() =>
            useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'all' }),
        );
        act(() => hook.result.current.setExplicit(['header-1']));
        expect(hook.result.current.selection).toMatchObject({ headerIds: ['header-1'] });
        return hook;
    }

    function pressEscapeOn(target: HTMLElement) {
        act(() => {
            target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
        });
    }

    it('keeps the selection when a button\'s Escape was already handled', () => {
        const { result } = selectOne();
        const save = document.createElement('button');
        // What the editor does with the Escape it cancels on.
        save.addEventListener('keydown', (e) => e.preventDefault());
        document.body.append(save);

        pressEscapeOn(save);

        expect(result.current.selection).toMatchObject({ headerIds: ['header-1'] });
        save.remove();
    });

    it('clears the selection on an Escape nothing else handled, a button\'s included', () => {
        const { result } = selectOne();
        const button = document.createElement('button');
        document.body.append(button);

        pressEscapeOn(button);

        expect(result.current.selection).toMatchObject({ headerIds: [] });
        button.remove();
    });
});
