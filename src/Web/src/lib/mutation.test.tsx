import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { useMutation } from './mutation';

// An error stays shown until the new result replaces or clears it (ADR-0023
// §L). TanStack clears a mutation's error as the next mutate starts; the SPA's
// useMutation keeps the last run's error while a retry is in flight.

/**
 * Runs the test settles one by one: each call waits for its own answer. A
 * mutation calls its function a few microtasks after mutate(), so an answer
 * first lets those run.
 */
function answeredByTest() {
    const answers: Array<{ resolve: () => void; reject: (e: Error) => void }> = [];
    const send = () => new Promise<void>((resolve, reject) => { answers.push({ resolve, reject }); });
    const next = async () => {
        for (let i = 0; i < 20 && answers.length === 0; i += 1) await Promise.resolve();
        return answers.shift()!;
    };
    return {
        send,
        answer: async () => { (await next()).resolve(); },
        refuse: async (message: string) => { (await next()).reject(new Error(message)); },
    };
}

function wrapper({ children }: { children: ReactNode }) {
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('useMutation', () => {
    it('keeps the last refusal while a retry is in flight, and clears it when the retry succeeds', async () => {
        const { send, answer, refuse } = answeredByTest();
        const { result } = renderHook(() => useMutation({ mutationFn: send }), { wrapper });

        act(() => { result.current.mutate(); });
        await act(async () => { await refuse('Refused.'); });
        await waitFor(() => expect(result.current.error?.message).toBe('Refused.'));

        act(() => { result.current.mutate(); });
        await waitFor(() => expect(result.current.isPending).toBe(true));
        expect(result.current.error?.message).toBe('Refused.');
        expect(result.current.isError).toBe(true);

        await act(async () => { await answer(); });
        await waitFor(() => expect(result.current.isPending).toBe(false));
        expect(result.current.error).toBeNull();
        expect(result.current.isError).toBe(false);
    });

    it('replaces the refusal with the retry\'s own', async () => {
        const { send, refuse } = answeredByTest();
        const { result } = renderHook(() => useMutation({ mutationFn: send }), { wrapper });

        act(() => { result.current.mutate(); });
        await act(async () => { await refuse('First.'); });
        await waitFor(() => expect(result.current.error?.message).toBe('First.'));
        act(() => { result.current.mutate(); });
        await waitFor(() => expect(result.current.isPending).toBe(true));
        expect(result.current.error?.message).toBe('First.');

        await act(async () => { await refuse('Second.'); });
        await waitFor(() => expect(result.current.error?.message).toBe('Second.'));
    });

    it('clears the refusal at once on reset, for a page that starts over', async () => {
        const { send, refuse } = answeredByTest();
        const { result } = renderHook(() => useMutation({ mutationFn: send }), { wrapper });

        act(() => { result.current.mutate(); });
        await act(async () => { await refuse('Refused.'); });
        await waitFor(() => expect(result.current.error?.message).toBe('Refused.'));
        act(() => { result.current.reset(); });
        await waitFor(() => expect(result.current.error).toBeNull());
        expect(result.current.isError).toBe(false);
    });

    it('shows no error on a first run that is in flight', async () => {
        const { send } = answeredByTest();
        const { result } = renderHook(() => useMutation({ mutationFn: send }), { wrapper });
        act(() => { result.current.mutate(); });
        await waitFor(() => expect(result.current.isPending).toBe(true));
        expect(result.current.error).toBeNull();
        expect(result.current.isError).toBe(false);
    });
});
