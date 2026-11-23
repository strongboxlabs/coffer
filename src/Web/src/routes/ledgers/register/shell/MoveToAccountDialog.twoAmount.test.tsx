import { useMemo, useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { AccountSummary } from '@/lib/types/account';
import type { UseSelectionResult } from '@/lib/useSelection';

import { MoveToAccountDialog } from './MoveToAccountDialog';
import { useRegisterBulkRecovery } from './useRegisterBulkRecovery';

/**
 * A move that leaves mirrored transfers across currencies, so each now keeps two amounts
 * (ADR-0114 D13). The move answers how many (`becameTwoAmount`). Above 0, the Move
 * dialog stays open with the line and a single Done in place of its picker, its title as
 * it was; Done and Escape close it; the selection is cleared and the register refreshed
 * at the move, not on Done (decided by the owner, 2026-11-18). At 0 it closes, as every
 * move did. The dialog and the hook both registers share, mocked at `fetch`; every
 * account is invented.
 */

const LEDGER = '00000000-0000-0000-0000-000000000010';
const SOURCE = '00000000-0000-0000-0000-000000000020';
const TARGET = '00000000-0000-0000-0000-000000000021';
const MOVE = /\/transactions\/bulk-move-account$/;
const SINGULAR = "1 transfer now keeps two amounts, one in each account's currency.";
const PLURAL = "3 transfers now keep two amounts, one in each account's currency.";

function account(id: string, name: string): AccountSummary {
    return {
        id, ledgerId: LEDGER, parentId: null, name, accountType: 'bank', categoryKind: null,
        currencyCode: 'USD', isActive: true, isSystem: false, feedConnectionId: null,
        needsReviewCount: 0, holdingsAccountId: null, isTradeCommission: false, institutionName: null,
    };
}
const ACCOUNTS = [account(SOURCE, 'Account A'), account(TARGET, 'Account B')];

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
});
afterEach(() => {
    vi.unstubAllGlobals();
});

/** A register's half: the shared hook, its dialog, and a selection of two that clears. */
function renderMove() {
    const cleared = vi.fn();
    const onRefresh = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
    function Register() {
        const [count, setCount] = useState(2);
        const selection = useMemo(() => ({
            selection: { kind: 'explicit', accountId: SOURCE, headerIds: ['h-1', 'h-2'] },
            clear: () => { cleared(); setCount(0); },
        }) as unknown as UseSelectionResult, []);
        const recovery = useRegisterBulkRecovery({ ledgerId: LEDGER, accountId: SOURCE, selection, onRefresh });
        return (
            <>
                <button type="button" onClick={recovery.openMoveDialog}>Open move</button>
                <MoveToAccountDialog
                    open={recovery.moveDialogOpen}
                    accounts={ACCOUNTS}
                    sourceAccountId={SOURCE}
                    sourceAccountType="bank"
                    count={count}
                    pending={recovery.bulkMovePending}
                    error={recovery.moveError}
                    becameTwoAmount={recovery.moveBecameTwoAmount}
                    onConfirm={recovery.onMoveConfirm}
                    onCancel={recovery.closeMoveDialog}
                />
            </>
        );
    }
    render(
        <QueryClientProvider client={queryClient}>
            <Register />
        </QueryClientProvider>,
    );
    const keys = () => invalidated.mock.calls.map((c) => c[0]?.queryKey);
    return { cleared, onRefresh, keys };
}

/** Open the dialog, pick Account B and press Move, which the stubbed move answers. */
async function moveToAccountB(answer: unknown) {
    stub.on('POST', MOVE, answer);
    const user = userEvent.setup();
    const rendered = renderMove();
    await user.click(screen.getByRole('button', { name: 'Open move' }));
    fireEvent.focus(screen.getByRole('combobox', { name: /Target account for move/i }));
    fireEvent.mouseDown(screen.getByRole('option', { name: /Account B/ }));
    await user.click(screen.getByRole('button', { name: 'Move' }));
    await waitFor(() => expect(stub.callsTo('POST', MOVE).length).toBe(1));
    expect(stub.callsTo('POST', MOVE)[0]!.body).toEqual(expect.objectContaining({ targetAccountId: TARGET }));
    return { user, ...rendered };
}

describe('the Move dialog after a move that left transfers keeping two amounts', () => {
    it.each([
        ['none did', { moved: 2, becameTwoAmount: 0 }],
        ['the count is absent', { moved: 2 }],
    ])('closes as before when %s', async (_label, answer) => {
        const { cleared, onRefresh } = await moveToAccountB(answer);

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        expect(cleared).toHaveBeenCalledTimes(1);
        expect(onRefresh).toHaveBeenCalledTimes(1);
        expect(screen.queryByText(/now keeps? two amounts/)).not.toBeInTheDocument();
    });

    it('stays open on the line for one transfer and Done, with the move\'s work already done', async () => {
        const { cleared, onRefresh, keys } = await moveToAccountB({ moved: 2, becameTwoAmount: 1 });

        const status = await screen.findByRole('status');
        expect(status).toHaveTextContent(SINGULAR);
        const dialog = screen.getByRole('dialog');
        // The title reads as it did when Move was pressed, though the selection is cleared.
        expect(screen.getByRole('heading', { name: 'Move 2 transactions to…' })).toBeInTheDocument();
        // The picker and its buttons are gone, Done alone in their place.
        expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Move' })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
        const done = screen.getByRole('button', { name: 'Done' });
        expect(dialog.querySelectorAll('button')).toHaveLength(1);
        expect(done).toHaveFocus();
        // Cleared and refreshed at the move, not on Done.
        expect(cleared).toHaveBeenCalledTimes(1);
        expect(onRefresh).toHaveBeenCalledTimes(1);
        expect(keys()).toContainEqual(['accounts', LEDGER]);
        expect(keys()).toContainEqual(['home', LEDGER]);
    });

    it('says how many for more than one', async () => {
        await moveToAccountB({ moved: 4, becameTwoAmount: 3 });

        expect(await screen.findByRole('status')).toHaveTextContent(PLURAL);
    });

    it('closes on Done, and opens on the picker next time', async () => {
        const { user } = await moveToAccountB({ moved: 2, becameTwoAmount: 1 });

        await user.click(await screen.findByRole('button', { name: 'Done' }));
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: 'Open move' }));
        expect(screen.getByRole('combobox', { name: /Target account for move/i })).toBeInTheDocument();
        expect(screen.queryByRole('status')).not.toBeInTheDocument();
    });

    it('closes on Escape', async () => {
        const { user } = await moveToAccountB({ moved: 2, becameTwoAmount: 1 });
        await waitFor(() => expect(screen.getByRole('button', { name: 'Done' })).toHaveFocus());

        await user.keyboard('{Escape}');

        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('opens on the picker next time when it was closed before the move landed', async () => {
        stub.on('POST', MOVE, { moved: 2, becameTwoAmount: 2 });
        const answer = globalThis.fetch;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            if (MOVE.test(new URL(raw, 'http://localhost').pathname)) await held;
            return answer(input, init);
        });
        const user = userEvent.setup();
        const { cleared } = renderMove();
        await user.click(screen.getByRole('button', { name: 'Open move' }));
        fireEvent.focus(screen.getByRole('combobox', { name: /Target account for move/i }));
        fireEvent.mouseDown(screen.getByRole('option', { name: /Account B/ }));
        await user.click(screen.getByRole('button', { name: 'Move' }));

        // Closed while the move is still saving; then it lands.
        await user.keyboard('{Escape}');
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
        release();
        await waitFor(() => expect(cleared).toHaveBeenCalledTimes(1));
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

        await user.click(screen.getByRole('button', { name: 'Open move' }));
        expect(screen.getByRole('combobox', { name: /Target account for move/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Move' })).toBeInTheDocument();
        expect(screen.queryByRole('status')).not.toBeInTheDocument();
    });

    it.each([
        ['left transfers keeping two amounts', { moved: 2, becameTwoAmount: 2 }],
        ['left none', { moved: 2, becameTwoAmount: 0 }],
    ])('a move that %s and lands after its dialog was closed and opened again leaves the new dialog on its picker', async (_label, answer) => {
        stub.on('POST', MOVE, answer);
        const passOn = globalThis.fetch;
        let release: () => void = () => {};
        const held = new Promise<void>((resolve) => { release = resolve; });
        vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
            if (MOVE.test(new URL(raw, 'http://localhost').pathname)) await held;
            return passOn(input, init);
        });
        const user = userEvent.setup();
        const { cleared } = renderMove();
        await user.click(screen.getByRole('button', { name: 'Open move' }));
        fireEvent.focus(screen.getByRole('combobox', { name: /Target account for move/i }));
        fireEvent.mouseDown(screen.getByRole('option', { name: /Account B/ }));
        await user.click(screen.getByRole('button', { name: 'Move' }));

        // Closed while the move is still saving, opened again, and then the move lands.
        await user.keyboard('{Escape}');
        await user.click(screen.getByRole('button', { name: 'Open move' }));
        release();
        await waitFor(() => expect(cleared).toHaveBeenCalledTimes(1));

        expect(screen.getByRole('dialog')).toBeInTheDocument();
        expect(screen.getByRole('combobox', { name: /Target account for move/i })).toBeInTheDocument();
        expect(screen.queryByRole('status')).not.toBeInTheDocument();
    });
});
