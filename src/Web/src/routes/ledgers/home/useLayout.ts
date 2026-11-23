import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import { ApiError, deleteDashboardPrefs, fetchDashboardPrefs, saveDashboardPrefs } from '@/lib/api';
import type { StoredDashboardPrefs } from '@/lib/types';

import type { Column } from './dashboardCatalog';
import { resolveLayout, toDocument, type Layout } from './dashboardLayout';

const key = (ledgerId: string) => ['dashboard-prefs', ledgerId] as const;

/**
 * The home screen's layout: the stored document resolved into a complete layout,
 * and the writes. Every save echoes the `updatedAt` it was read at, so a save made
 * over somebody else's (another tab, another session) is refused rather than
 * erasing it; the refusal opens a choice between theirs and mine.
 */
export function useLayout(ledgerId: string) {
    const queryClient = useQueryClient();
    const query = useQuery({ queryKey: key(ledgerId), queryFn: () => fetchDashboardPrefs(ledgerId) });
    const layout = useMemo(() => resolveLayout(query.data), [query.data]);
    const [stale, setStale] = useState<Layout | null>(null);

    const save = useMutation({
        // The updatedAt is read when the save runs, not when it was rendered: a
        // "Keep mine" re-save has to carry the one the refetch just brought back.
        mutationFn: (next: Layout) => {
            const current = queryClient.getQueryData<StoredDashboardPrefs>(key(ledgerId));
            return saveDashboardPrefs(ledgerId, toDocument(next), current?.updatedAt ?? null);
        },
        onSuccess: (saved) => {
            queryClient.setQueryData(key(ledgerId), saved);
            setStale(null);
        },
        onError: (error, next) => {
            if (error instanceof ApiError && error.code === 'dashboard-prefs-stale') {
                setStale(next);
                void query.refetch();
            }
        },
    });

    const reset = useMutation({
        mutationFn: () => deleteDashboardPrefs(ledgerId),
        onSuccess: () => {
            queryClient.setQueryData<StoredDashboardPrefs>(key(ledgerId), { widgets: [], updatedAt: null });
        },
    });

    return {
        query,
        layout,
        /** The stored layout loaded: only then may anything be written over it. */
        writable: query.isSuccess,
        /** The stored layout is still being read: what writes it waits on the read. */
        loading: query.isPending,
        /** The stored layout could not be read: nothing may be written over it. */
        failed: query.isError,
        save,
        reset,
        /** A save refused as stale: the layout that was being saved. */
        stale,
        reloadTheirs: () => setStale(null),
        /** Save the refused layout again over the one now stored; `onSaved` runs when
         *  it lands (edit mode leaves then). */
        keepMine: (onSaved?: () => void) => { if (stale) save.mutate(stale, { onSuccess: onSaved }); },
    };
}

/** The layout with one widget changed. */
export function withWidget(layout: Layout, id: string, change: (w: Layout['widgets'][number]) => Layout['widgets'][number]): Layout {
    return { ...layout, widgets: layout.widgets.map((w) => (w.id === id ? change(w) : w)) };
}

/**
 * The layout with one widget put back as it was in `before`: its visibility, column,
 * settings and place in the list, whatever else has changed since.
 */
export function restoredWidget(layout: Layout, before: Layout, id: string): Layout {
    const at = before.widgets.findIndex((w) => w.id === id);
    if (at < 0) return layout;
    const widgets = layout.widgets.filter((w) => w.id !== id);
    widgets.splice(Math.min(at, widgets.length), 0, before.widgets[at]!);
    return { ...layout, widgets };
}

/**
 * The layout with one widget moved within its column by `delta` places among the
 * visible widgets there, or to the end of `column` when one is given.
 */
export function moved(layout: Layout, id: string, move: { delta: number } | { column: Column; index?: number }): Layout {
    const widgets = [...layout.widgets];
    const from = widgets.findIndex((w) => w.id === id);
    if (from < 0) return layout;
    const widget = widgets[from]!;

    if ('delta' in move) {
        // Swap with the neighbouring VISIBLE widget in the same column; hidden ones
        // keep their places in the list and are stepped over.
        const peers = widgets.map((w, i) => ({ w, i })).filter(({ w }) => w.column === widget.column && w.visible);
        const at = peers.findIndex(({ i }) => i === from);
        const target = peers[at + move.delta];
        if (!target) return layout;
        widgets[from] = target.w;
        widgets[target.i] = widget;
        return { ...layout, widgets };
    }

    widgets.splice(from, 1);
    const updated = { ...widget, column: move.column };
    const peers = widgets.map((w, i) => ({ w, i })).filter(({ w }) => w.column === move.column && w.visible);
    const index = move.index ?? peers.length;
    const insertAt = index >= peers.length
        ? (peers.length > 0 ? peers[peers.length - 1]!.i + 1 : widgets.length)
        : peers[index]!.i;
    widgets.splice(insertAt, 0, updated);
    return { ...layout, widgets };
}
