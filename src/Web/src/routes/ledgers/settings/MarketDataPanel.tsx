import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { useId } from 'react';

import {
    fetchQuoteProviders,
    fetchQuotesPrefs,
    fetchSchedule,
    saveQuotesPrefs,
    saveSchedule,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { useLedgerRole } from '@/lib/useLedgerRole';
import type { QuotesPrefs } from '@/lib/types';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { Checkbox } from '@/components/ui/Checkbox';
import { PendingScope } from '@/components/ui/pending';
import { ScheduleControl } from '@/components/ScheduleControl';

import { BenchmarksSection } from './BenchmarksSection';

/** The Yahoo Finance provider's key: the prices and benchmarks provider (ADR-0114 D8). */
const YAHOO = 'yahoo';
/** Frankfurter's key: the exchange-rate provider (ADR-0114 D4, D8). */
const FRANKFURTER = 'frankfurter';

/** ` (host)`, the host a provider's requests go to, or nothing when it is not known. */
const at = (host: string | null) => (host ? ` (${host})` : '');

/** What each provider fetches and sends, and where, under its name (the data-sources
 *  board): the host in brackets is the install's own, so one that points Frankfurter
 *  at its own container shows that. A provider the SPA does not know yet shows its
 *  name alone. */
const PROVIDER_LINES: Record<string, (host: string | null) => readonly string[]> = {
    [YAHOO]: (host) => [
        `End-of-day prices from Yahoo Finance${at(host)} for holdings that have a public ticker.`,
        "Also checks and fetches this ledger's benchmark funds for the Returns report.",
    ],
    [FRANKFURTER]: (host) => [
        `Daily exchange rates from Frankfurter${at(host)}, so totals that combine accounts in different currencies are converted into the ledger's reporting currency.`,
        'Sent: currency codes and dates only. No amounts and no account data.',
    ],
};

/**
 * Market-data settings (ADR-0057): per-ledger opt-in for external quote
 * providers. A provider stays off until the user enables it here — only then
 * does a price refresh reach out to it. The no-egress SimpleFIN-holdings prices
 * are always on and aren't listed. One list (ADR-0114 D8): Yahoo Finance for
 * prices and benchmarks, then Frankfurter for exchange rates, each saying what it
 * fetches. The ledger's benchmarks sit under it, beside the switch adding one needs.
 */
export function MarketDataPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const yahooSwitchId = useId();
    // The providers are the reader's own choice, which a viewer makes too; the
    // ledger's refresh schedule is an owner's or an editor's (PUT /schedules).
    const { canEdit } = useLedgerRole(ledgerId);
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
            // Turning Frankfurter on starts a backfill of the ledger's rates (ADR-0114
            // D11), which the Exchange rates tab shows as they land; the benchmark
            // list's availability reads the reader's Yahoo switch and those rates.
            void queryClient.invalidateQueries({ queryKey: ['fx-rates', ledgerId] });
            void queryClient.invalidateQueries({ queryKey: ['benchmarks', ledgerId] });
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
                    Choose which external providers may fetch data for this ledger:
                    end-of-day prices, and the exchange rates that convert totals
                    across currencies. Each makes outbound calls to a third-party
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
                        // Every switch writes the whole list, built from the last
                        // one read: one group (pending.ts), so a change of any
                        // holds them all until its save has landed, and a second
                        // change never drops the first.
                        <PendingScope>
                        <ul className="divide-y divide-border/60 rounded border border-border">
                            {providersQuery.data.map((p) => (
                                <li
                                    key={p.key}
                                    className="flex items-start justify-between gap-3 px-3 py-2.5"
                                >
                                    <div className="min-w-0 space-y-0.5">
                                        <span className="text-sm font-medium">{p.displayName}</span>
                                        {(PROVIDER_LINES[p.key]?.(p.host) ?? []).map((line) => (
                                            <p key={line} className="text-xs text-text-muted">{line}</p>
                                        ))}
                                    </div>
                                    <label className="flex shrink-0 items-center gap-2 text-sm text-text-muted">
                                        <span>{enabled.includes(p.key) ? 'On' : 'Off'}</span>
                                        <Checkbox
                                            id={p.key === YAHOO ? yahooSwitchId : undefined}
                                            checked={enabled.includes(p.key)}
                                            aria-label={`Enable ${p.displayName}`}
                                            pending={mutation.isPending}
                                            onChange={(e) => toggle(p.key, e.target.checked)}
                                        />
                                    </label>
                                </li>
                            ))}
                        </ul>
                        </PendingScope>
                    )}

                    {canEdit ? (
                        <div className="border-t border-border/60 pt-3">
                            <ScheduleControl
                                queryKey={['schedule', ledgerId, 'quote-refresh']}
                                load={() => fetchSchedule(ledgerId, 'quote-refresh')}
                                save={(body) => saveSchedule(ledgerId, 'quote-refresh', body)}
                                label="Refresh prices and exchange rates automatically each day"
                                note="uses the providers enabled above"
                            />
                        </div>
                    ) : null}
                </PanelBody>
            </Panel>

            <BenchmarksSection
                ledgerId={ledgerId}
                canEdit={canEdit}
                yahooOn={prefsQuery.isSuccess ? enabled.includes(YAHOO) : undefined}
                onFocusYahoo={() => document.getElementById(yahooSwitchId)?.focus()}
            />
        </div>
    );
}
