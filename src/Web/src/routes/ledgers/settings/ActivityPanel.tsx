import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';

import { fetchLedgerOperations } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import type { LedgerOperationSummary } from '@/lib/types';
import {
    familyClass,
    formatRelative,
    ledgerOperationLabel,
    statusClass,
    summarizeLedgerOperation,
    triggerLabel,
    whoLabel,
} from '@/lib/ledgerOperationDisplay';
import { EmptyStateInline } from '@/components/ui/EmptyState';
import { Panel, PanelBody } from '@/components/ui/Panel';

const PROVIDER_OPTIONS = [
    { value: '', label: 'All operations' },
    { value: 'simplefin', label: 'SimpleFIN' },
    { value: 'ofx', label: 'OFX' },
    { value: 'qif', label: 'QIF' },
    { value: 'file', label: 'File import' },
    { value: 'moneydance', label: 'Moneydance import' },
    { value: 'quote-refresh', label: 'Quotes' },
    { value: 'snapshot-restore', label: 'Snapshot restore' },
] as const;

const DAYS_OPTIONS = [
    { value: 1, label: 'Last day' },
    { value: 3, label: 'Last 3 days' },
    { value: 7, label: 'Last week' },
    { value: 30, label: 'Last 30 days' },
    { value: 0, label: 'All time' },
] as const;

/**
 * Settings → Activity (ADR-0069 nav swap; was the standalone `/activity` page,
 * ADR-0055 slice C). Ledger-wide ledger-operation timeline — every ingest
 * (SimpleFIN / OFX / QIF / Moneydance import), quote refresh, and snapshot
 * restore, newest first, filterable by operation + recency. Defaults to all
 * operations / last 3 days.
 *
 * Section-heading Settings-tab layout (the shared idiom): an <h2> + description
 * header, then the filter row + the operations list below.
 */
export function ActivityPanel({ ledgerId }: { ledgerId: string }) {
    const [provider, setProvider] = useState('');
    const [days, setDays] = useState(3);

    const runsQuery = useQuery({
        queryKey: ['ledger-operations', ledgerId, provider, days],
        queryFn: () =>
            fetchLedgerOperations(ledgerId, {
                provider: provider || undefined,
                days: days > 0 ? days : undefined,
            }),
    });

    const runs = runsQuery.data;

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Activity</h2>
                <p className="text-sm text-text-muted">
                    Every operation on this ledger — bank syncs, file &amp;
                    Moneydance imports, price refreshes, and snapshot restores —
                    newest first.
                </p>
            </header>

            <div className="flex flex-wrap items-center gap-3">
                <label className="flex items-center gap-1.5 text-sm">
                    <span className="text-text-muted">Provider</span>
                    <select
                        value={provider}
                        onChange={(e) => setProvider(e.target.value)}
                        className="rounded border border-border bg-surface px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    >
                        {PROVIDER_OPTIONS.map((o) => (
                            <option key={o.value} value={o.value}>
                                {o.label}
                            </option>
                        ))}
                    </select>
                </label>
                <label className="flex items-center gap-1.5 text-sm">
                    <span className="text-text-muted">Period</span>
                    <select
                        value={days}
                        onChange={(e) => setDays(Number(e.target.value))}
                        className="rounded border border-border bg-surface px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    >
                        {DAYS_OPTIONS.map((o) => (
                            <option key={o.value} value={o.value}>
                                {o.label}
                            </option>
                        ))}
                    </select>
                </label>
                <span className="ml-auto whitespace-nowrap font-mono text-[0.6875rem] tabular-nums text-text-muted">
                    {runs?.length ?? 0} runs
                </span>
            </div>

            {runsQuery.isError ? (
                <Panel className="border-state-danger/40 bg-state-danger-soft">
                    <PanelBody>
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(runsQuery.error, 'Could not load activity.')}
                        </p>
                    </PanelBody>
                </Panel>
            ) : null}

            <Panel>
                {runsQuery.isPending ? (
                    <PanelBody>
                        <p className="text-sm text-text-subtle">Loading…</p>
                    </PanelBody>
                ) : !runs || runs.length === 0 ? (
                    <EmptyStateInline
                        className="py-10"
                        message="No activity in this period."
                        hint="Try a wider period or a different provider."
                    />
                ) : (
                    <ul className="divide-y divide-border/60">
                        {runs.map((r) => (
                            <ActivityRow key={r.id} run={r} />
                        ))}
                    </ul>
                )}
            </Panel>
        </div>
    );
}

function ActivityRow({ run }: { run: LedgerOperationSummary }) {
    return (
        <li className="flex items-start gap-3 px-4 py-2.5 text-sm">
            <span
                className={
                    'mt-0.5 rounded px-1.5 py-0.5 text-[0.625rem] font-semibold uppercase tracking-wider ' +
                    familyClass(run.family)
                }
            >
                {run.family}
            </span>
            <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
                    <span className="font-medium">{ledgerOperationLabel(run.providerKey)}</span>
                    <span
                        className={
                            'rounded-full px-1.5 py-0.5 text-[0.625rem] font-semibold uppercase tracking-wider ' +
                            statusClass(run.status)
                        }
                    >
                        {run.status}
                    </span>
                    <span className="text-[0.6875rem] text-text-subtle">
                        {formatRelative(run.startedAt)} · {whoLabel(run.triggeredByUserId)} ·{' '}
                        {triggerLabel(run.triggeredVia)}
                    </span>
                </div>
                <div className="mt-0.5 text-text-muted">
                    {summarizeLedgerOperation(run)}
                    {run.errorCount > 0 ? (
                        <span className="ml-1 text-state-danger">
                            · {run.errorCount} error{run.errorCount === 1 ? '' : 's'}
                        </span>
                    ) : null}
                </div>
            </div>
        </li>
    );
}
