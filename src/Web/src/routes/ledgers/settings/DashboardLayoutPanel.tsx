import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowDown, ArrowUp } from 'lucide-react';

import { fetchDashboardPrefs, saveDashboardPrefs } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import type { DashboardPrefs } from '@/lib/types';
import { resolveDashboardLayout, type ResolvedWidget } from '@/lib/dashboardWidgets';
import { Panel, PanelBody } from '@/components/ui/Panel';

/**
 * Dashboard layout settings (ADR-0056 slice 3): reorder + show/hide the
 * Overview widgets, persisted to the `dashboard` preference. Accounts is the
 * navigation backbone — reorderable but always shown.
 */
export function DashboardLayoutPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const prefsQuery = useQuery({
        queryKey: ['dashboard-prefs', ledgerId],
        queryFn: () => fetchDashboardPrefs(ledgerId),
    });
    const mutation = useMutation({
        mutationFn: (next: DashboardPrefs) => saveDashboardPrefs(ledgerId, next),
        onSuccess: (saved) => {
            queryClient.setQueryData(['dashboard-prefs', ledgerId], saved);
            // The Overview reads the same pref — keep it in sync.
            queryClient.invalidateQueries({ queryKey: ['dashboard-prefs', ledgerId] });
        },
    });

    const layout = resolveDashboardLayout(prefsQuery.data);

    function save(next: ResolvedWidget[]) {
        mutation.mutate({
            widgets: next.map((w) => ({ key: w.key, visible: w.visible })),
        });
    }

    function move(index: number, delta: number) {
        const next = [...layout];
        const target = index + delta;
        if (target < 0 || target >= next.length) return;
        [next[index], next[target]] = [next[target], next[index]];
        save(next);
    }

    function toggle(index: number) {
        const next = layout.map((w, i) =>
            i === index && !w.alwaysVisible ? { ...w, visible: !w.visible } : w,
        );
        save(next);
    }

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Dashboard layout</h2>
                <p className="text-sm text-text-muted">
                    Reorder with the arrows and toggle widgets on or off. Changes
                    apply to this ledger's Overview.
                </p>
            </header>

            <Panel>
                <PanelBody className="space-y-3">
                    {prefsQuery.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(prefsQuery.error, 'Could not update the dashboard layout.')}
                        </p>
                    ) : null}
                    {mutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(mutation.error, 'Could not update the dashboard layout.')}
                        </p>
                    ) : null}

                    {prefsQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : (
                        <ul className="divide-y divide-border/60 rounded border border-border">
                            {layout.map((w, i) => (
                                <li
                                    key={w.key}
                                    className="flex items-center gap-3 px-3 py-2.5"
                                >
                                    <span className="flex flex-col">
                                        <button
                                            type="button"
                                            aria-label={`Move ${w.label} up`}
                                            disabled={i === 0 || mutation.isPending}
                                            onClick={() => move(i, -1)}
                                            className="text-text-muted hover:text-text disabled:opacity-30"
                                        >
                                            <ArrowUp className="h-3.5 w-3.5" aria-hidden />
                                        </button>
                                        <button
                                            type="button"
                                            aria-label={`Move ${w.label} down`}
                                            disabled={i === layout.length - 1 || mutation.isPending}
                                            onClick={() => move(i, 1)}
                                            className="text-text-muted hover:text-text disabled:opacity-30"
                                        >
                                            <ArrowDown className="h-3.5 w-3.5" aria-hidden />
                                        </button>
                                    </span>
                                    <span className="flex-1 text-sm font-medium">{w.label}</span>
                                    {w.alwaysVisible ? (
                                        <span className="text-[0.6875rem] uppercase tracking-wider text-text-subtle">
                                            always shown
                                        </span>
                                    ) : (
                                        <label className="flex items-center gap-2 text-sm text-text-muted">
                                            <span>{w.visible ? 'Shown' : 'Hidden'}</span>
                                            <input
                                                type="checkbox"
                                                checked={w.visible}
                                                disabled={mutation.isPending}
                                                onChange={() => toggle(i)}
                                                className="h-4 w-4 rounded border-border text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                                aria-label={`Show ${w.label}`}
                                            />
                                        </label>
                                    )}
                                </li>
                            ))}
                        </ul>
                    )}
                </PanelBody>
            </Panel>
        </div>
    );
}
