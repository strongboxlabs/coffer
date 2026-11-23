import { useState } from 'react';
import {
    useMutation as useQueryMutation,
    type DefaultError,
    type QueryClient,
    type UseMutationOptions,
    type UseMutationResult,
} from '@tanstack/react-query';

/**
 * TanStack Query's useMutation, with one change: a refusal stays shown while a
 * retry is in flight (ADR-0023 §L). The SPA takes useMutation from here, never
 * from TanStack directly.
 *
 * THE RULE. An error stays shown until the new result replaces or clears it;
 * it is never cleared at the press. TanStack clears a mutation's `error` as the
 * next mutate starts, so an alert that reads it would go at the press of a
 * retry: the dialog or form shrinks, and a button below the alert moves from
 * under the pointer before the second press of a double click.
 *
 * WHAT CHANGES. While a retry is pending, `error` (and `isError`) is the last
 * run's error. The retry's own result replaces it: success clears it, a new
 * refusal takes its place. `reset()` still clears it at once, for a page that
 * starts over (a new file picked, an edit made). Nothing else differs:
 * `isPending` is true while the retry is in flight, so a pending button stays
 * pending; and the callbacks get the error of their own run.
 *
 * While a retry is in flight the result is pending and in error at once, which
 * TanStack's result type has no member for, so the type says what TanStack
 * would: narrowed by isPending, `error` reads as null. Code reads `error` to
 * show it, not inside an isPending branch.
 *
 * A page that swaps its controls on `isError` (a failure view with its own
 * Try again and Close) therefore keeps showing them during the retry, and
 * gives each of them `pending={isPending}`, so the retry holds them as the
 * resting view's buttons were held.
 */
export function useMutation<TData = unknown, TError = DefaultError, TVariables = void, TOnMutateResult = unknown>(
    options: UseMutationOptions<TData, TError, TVariables, TOnMutateResult>,
    queryClient?: QueryClient,
): UseMutationResult<TData, TError, TVariables, TOnMutateResult> {
    const result = useQueryMutation(options, queryClient);
    // The last settled run's error, followed on every render that is not pending.
    const [settled, setSettled] = useState<TError | null>(result.error);
    if (!result.isPending && settled !== result.error) setSettled(result.error);
    if (!result.isPending || settled === null) return result;
    return { ...result, error: settled, isError: true } as unknown as UseMutationResult<TData, TError, TVariables, TOnMutateResult>;
}
