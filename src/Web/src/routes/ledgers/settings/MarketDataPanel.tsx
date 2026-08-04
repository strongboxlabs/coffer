import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    fetchQuoteProviders,
    fetchQuotesPrefs,
    fetchSchedule,
    saveQuotesPrefs,
    saveSchedule,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import type { QuotesPrefs } from '@/lib/types';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { ScheduleControl } from '@/components/ScheduleControl';

/**
 * Market-data settings (ADR-0057): per-ledger opt-in for external quote
 * providers. A provider stays off until the user enables it here — only then
 * does a price refresh reach out to it. The no-egress SimpleFIN-holdings prices
 * are always on and aren't listed.
 */
export function MarketDataPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const providersQuery = useQuery({
        queryKey: ['quote-providers', ledgerId],
        queryFn: () => fetchQuoteProviders(ledgerId),
    });
    const prefsQuery = useQuery({
        queryKey: ['quotes-prefs', ledgerId],
        queryFn: () => fetchQuotesPrefs(ledgerId),
    });

    const mutation = useMutation({
        mutationFn: (next: QuotesPrefs) => saveQuotesPrefs(ledgerId, next),
        onSuccess: (saved) => {
            queryClient.setQueryData(['quotes-prefs', ledgerId], saved);
        },
    });

    const enabled = prefsQuery.data?.enabledProviders ?? [];

    function toggle(key: string, on: boolean) {
        const next = on
            ? Array.from(new Set([...enabled, key]))
            : enabled.filter((k) => k !== key);
        mutation.mutate({ enabledProviders: next });
    }

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Quotes</h2>
                <p className="text-sm text-text-muted">
                    Choose which external providers may fetch end-of-day prices
                    for this ledger. Each makes outbound calls to a third-party
                    service only when enabled; prices derived from your bank syncs
                    need no provider and always work.
                </p>
            </header>

            <Panel>
                <PanelBody className="space-y-3">
                    {providersQuery.isError || prefsQuery.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(providersQuery.error ?? prefsQuery.error, 'Could not update market-data settings.')}
                        </p>
                    ) : null}

                    {mutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(mutation.error, 'Could not update market-data settings.')}
                        </p>
                    ) : null}

                    {providersQuery.isPending || prefsQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : !providersQuery.data || providersQuery.data.length === 0 ? (
                        <p className="text-sm text-text-muted">
                            No external market-data providers are available.
                        </p>
                    ) : (
                        <ul className="divide-y divide-border/60 rounded border border-border">
                            {providersQuery.data.map((p) => (
                                <li
                                    key={p.key}
                                    className="flex items-center justify-between gap-3 px-3 py-2.5"
                                >
                                    <span className="text-sm font-medium">{p.displayName}</span>
                                    <label className="flex items-center gap-2 text-sm text-text-muted">
                                        <span>{enabled.includes(p.key) ? 'On' : 'Off'}</span>
                                        <input
                                            type="checkbox"
                                            checked={enabled.includes(p.key)}
                                            disabled={mutation.isPending}
                                            onChange={(e) => toggle(p.key, e.target.checked)}
                                            className="h-4 w-4 rounded border-border text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                            aria-label={`Enable ${p.displayName}`}
                                        />
                                    </label>
                                </li>
                            ))}
                        </ul>
                    )}

                    <div className="border-t border-border/60 pt-3">
                        <ScheduleControl
                            queryKey={['schedule', ledgerId, 'quote-refresh']}
                            load={() => fetchSchedule(ledgerId, 'quote-refresh')}
                            save={(body) => saveSchedule(ledgerId, 'quote-refresh', body)}
                            label="Refresh prices automatically each day"
                            note="uses the providers enabled above"
                        />
                    </div>
                </PanelBody>
            </Panel>
        </div>
    );
}
