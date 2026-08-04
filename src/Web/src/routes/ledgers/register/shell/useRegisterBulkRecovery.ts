import { useCallback, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import { ApiError, bulkMoveToAccount, bulkUnhideTransactions } from '@/lib/api';
import type { BulkMoveAccountResponse, BulkUnhideResponse } from '@/lib/types';
import type { UseSelectionResult } from '@/lib/useSelection';

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

    const invalidateAfterBulk = useCallback(() => {
        selection.clear();
        onRefresh();
        queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
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

    const moveMutation = useMutation<BulkMoveAccountResponse, ApiError, string>({
        mutationFn: (targetAccountId) =>
            bulkMoveToAccount(ledgerId, selection.selection, targetAccountId),
        onSuccess: () => {
            setMoveDialogOpen(false);
            setMoveError(null);
            invalidateAfterBulk();
        },
        onError: (err) => setMoveError(err.detail || 'Move failed.'),
    });

    return {
        onBulkUnhide: useCallback(() => unhideMutation.mutate(), [unhideMutation]),
        bulkUnhidePending: unhideMutation.isPending,
        moveDialogOpen,
        openMoveDialog: useCallback(() => {
            setMoveError(null);
            setMoveDialogOpen(true);
        }, []),
        closeMoveDialog: useCallback(() => setMoveDialogOpen(false), []),
        onMoveConfirm: useCallback(
            (targetAccountId: string) => moveMutation.mutate(targetAccountId),
            [moveMutation],
        ),
        moveError,
        bulkMovePending: moveMutation.isPending,
    };
}
