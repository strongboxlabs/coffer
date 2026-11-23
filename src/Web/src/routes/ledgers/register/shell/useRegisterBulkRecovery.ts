import { useCallback, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import { ApiError, bulkMoveToAccount, bulkUnhideTransactions } from '@/lib/api';
import type { BulkMoveAccountResponse, BulkUnhideResponse } from '@/lib/types';
import type { UseSelectionResult } from '@/lib/useSelection';
import { invalidateHomeReads } from '@/lib/registerInvalidation';

/**
 * Bulk recovery actions (ADR-0072 D2/D3) shared by BOTH registers so they can't
 * drift (feedback: registers unified by default). Unhide returns the selection
 * to the register; Move relocates it to another account (all-or-nothing — the
 * server enforces the guards and the rejection surfaces in the move dialog).
 * Both bypass the SaveChanges interceptor server-side, so on success we clear
 * the selection, refresh the window, and invalidate the sidebar + bucket
 * queries (plus holdings on the investment register). One implementation; the
 * pages differ only by `invalidateHoldings`.
 */
export interface UseRegisterBulkRecoveryArgs {
    ledgerId: string;
    accountId: string;
    selection: UseSelectionResult;
    /** Re-seed the register window after a successful op (register.refresh). */
    onRefresh: () => void;
    /** Investment registers also invalidate holdings/lots. */
    invalidateHoldings?: boolean;
}

export interface UseRegisterBulkRecoveryResult {
    onBulkUnhide: () => void;
    bulkUnhidePending: boolean;
    moveDialogOpen: boolean;
    openMoveDialog: () => void;
    closeMoveDialog: () => void;
    onMoveConfirm: (targetAccountId: string) => void;
    moveError: string | null;
    bulkMovePending: boolean;
    /** How many transfers the landed move left keeping two amounts (ADR-0114 D13). Above
     *  0, the move dialog stays open on that line and Done; 0 otherwise. */
    moveBecameTwoAmount: number;
}

export function useRegisterBulkRecovery({
    ledgerId,
    accountId,
    selection,
    onRefresh,
    invalidateHoldings = false,
}: UseRegisterBulkRecoveryArgs): UseRegisterBulkRecoveryResult {
    const queryClient = useQueryClient();
    const [moveDialogOpen, setMoveDialogOpen] = useState(false);
    const [moveError, setMoveError] = useState<string | null>(null);
    const [moveBecameTwoAmount, setMoveBecameTwoAmount] = useState(0);
    // Each open or close of the move dialog starts a new session. A move answers
    // only the session it was pressed in, so one that lands after its dialog was
    // closed (or closed and opened again for another selection) neither closes
    // nor rewrites the dialog now showing.
    const moveSession = useRef(0);

    const invalidateAfterBulk = useCallback(() => {
        selection.clear();
        onRefresh();
        queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
        invalidateHomeReads(queryClient, ledgerId);
        queryClient.invalidateQueries({
            queryKey: ['register-index-buckets', ledgerId, accountId],
        });
        if (invalidateHoldings) {
            queryClient.invalidateQueries({ queryKey: ['holdings', ledgerId, accountId] });
        }
    }, [selection, onRefresh, queryClient, ledgerId, accountId, invalidateHoldings]);

    const unhideMutation = useMutation<BulkUnhideResponse, ApiError, void>({
        mutationFn: () => bulkUnhideTransactions(ledgerId, selection.selection),
        onSuccess: invalidateAfterBulk,
    });

    const moveMutation = useMutation<
        BulkMoveAccountResponse,
        ApiError,
        { targetAccountId: string; session: number }
    >({
        mutationFn: ({ targetAccountId }) =>
            bulkMoveToAccount(ledgerId, selection.selection, targetAccountId),
        onSuccess: (result, { session }) => {
            // The refresh happens whenever the move lands.
            invalidateAfterBulk();
            if (session !== moveSession.current) return;
            setMoveError(null);
            // A move that left transfers keeping two amounts says so, and the dialog
            // stays open on that line until it is closed (ADR-0114 D13, decided by the
            // owner, 2026-11-18); otherwise it closes.
            if (result.becameTwoAmount > 0) setMoveBecameTwoAmount(result.becameTwoAmount);
            else setMoveDialogOpen(false);
        },
        onError: (err, { session }) => {
            if (session === moveSession.current) setMoveError(err.detail || 'Move failed.');
        },
    });

    return {
        onBulkUnhide: useCallback(() => unhideMutation.mutate(), [unhideMutation]),
        bulkUnhidePending: unhideMutation.isPending,
        moveDialogOpen,
        // Opens on the picker, as a new session.
        openMoveDialog: useCallback(() => {
            moveSession.current += 1;
            setMoveError(null);
            setMoveBecameTwoAmount(0);
            setMoveDialogOpen(true);
        }, []),
        // Done and Escape close through here too; a move still saving no longer
        // answers this dialog.
        closeMoveDialog: useCallback(() => {
            moveSession.current += 1;
            setMoveDialogOpen(false);
            setMoveBecameTwoAmount(0);
        }, []),
        onMoveConfirm: useCallback(
            (targetAccountId: string) =>
                moveMutation.mutate({ targetAccountId, session: moveSession.current }),
            [moveMutation],
        ),
        moveError,
        bulkMovePending: moveMutation.isPending,
        moveBecameTwoAmount,
    };
}
