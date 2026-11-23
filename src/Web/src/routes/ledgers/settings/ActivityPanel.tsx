import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ChevronDown, ChevronRight } from 'lucide-react';

import { fetchFeedConnections, fetchLedgerOperations, fetchSyncRunDetail } from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { formatAsSent, formatCurrency } from '@/lib/money';
import type { LedgerOperationSummary, SyncErrorDto, SyncRunDetail } from '@/lib/types';
import {
    DEMO_EXAMPLES,
    MARKET_SERIES_BACKFILL,
    errorSourceLabel,
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

/**
 * THIS PANEL IS A LOG. It reports what ran; it does not act on it.
 *
 * No "Undo this import" affordance here. `undo-import` has exactly one caller —
 * the import dialog — so the undo lives only as long as that dialog stays open.
 * True, and not a reason to put an irreversible hard-delete of transactions
 * inside an audit log.
 *
 * Two things are wrong with it here. A log is read-only by nature: a destructive
 * control in a list of past events is a category error, and the one place a
 * reader does not expect to lose data. And the context is worst there — in the
 * import modal you have just seen what landed, whereas here you are looking at
 * a row from days ago summarised in one line.
 *
 * Undo belongs in ImportFileDialog, where it already is. If reaching it after
 * the dialog closes is a real need, that is a feature to design, not an
 * affordance to bolt onto the log.
 */

const PROVIDER_OPTIONS = [
    { value: '', label: 'All operations' },
    { value: 'simplefin', label: 'SimpleFIN' },
    { value: 'ofx', label: 'OFX' },
    { value: 'qif', label: 'QIF' },
    // A file run is recorded under its reader's key; the server matches the key exactly.
    { value: 'csv-generic', label: 'CSV import' },
    { value: 'csv-fidelity', label: 'Fidelity CSV import' },
    { value: 'moneydance', label: 'Moneydance import' },
    { value: 'demo', label: 'Demo examples' },
    { value: 'quote-refresh', label: 'Quotes' },
    { value: MARKET_SERIES_BACKFILL, label: 'Rates and benchmarks backfill' },
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
 * (SimpleFIN / OFX / QIF / Moneydance import, and a Demo's examples, ADR-0115
 * D3), quote refresh, and snapshot restore, newest first, filterable by
 * operation + recency. Defaults to all
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

    // Which bank a feed sync pulled, by the connection's own name, so a ledger with several
    // connections can tell their runs apart. The same cached list Bank feeds and the
    // sidebar read. A connection with no name of its own (it reached several institutions)
    // adds nothing to "SimpleFIN", and neither does one that has been removed.
    const connectionsQuery = useQuery({
        queryKey: ['feed-connections', ledgerId],
        queryFn: () => fetchFeedConnections(ledgerId),
    });
    const connectionNames = new Map(
        (connectionsQuery.data ?? [])
            .filter((c) => c.institutionName)
            .map((c) => [c.id, c.institutionName as string]),
    );

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Activity</h2>
                <p className="text-sm text-text-muted">
                    Every operation on this ledger — bank syncs, file &amp;
                    Moneydance imports, a Demo&apos;s examples, price refreshes, and
                    snapshot restores — newest first.
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
                            <ActivityRow
                                key={r.id}
                                run={r}
                                ledgerId={ledgerId}
                                connectionName={r.feedConnectionId ? connectionNames.get(r.feedConnectionId) : undefined}
                            />
                        ))}
                    </ul>
                )}
            </Panel>
        </div>
    );
}


/**
 * A run has something to open when it recorded errors, cleared pending charges (the
 * list says which cleared at a different amount), or ended without finishing: a failed
 * run's message is only on the detail, and a needs-reauth run opens to say what to do.
 * A Demo's examples row that stopped partway opens too: it records the examples not
 * added, and why, as its message, with no error rows (ADR-0115 D3). A partial run of
 * any other kind is not matched by status alone, since one without error rows would
 * open to nothing. A clean run has nothing behind its line, so it is not offered as
 * expandable.
 */
function hasRunDetail(run: LedgerOperationSummary): boolean {
    return (
        run.errorCount > 0
        || (run.details.txns_promoted ?? 0) > 0
        || run.status === 'failed'
        || run.status === 'needs_reauth'
        || (run.providerKey === DEMO_EXAMPLES && run.status === 'partial')
    );
}

function ActivityRow({
    run,
    ledgerId,
    connectionName,
}: {
    run: LedgerOperationSummary;
    ledgerId: string;
    connectionName: string | undefined;
}) {
    const [expanded, setExpanded] = useState(false);
    const expandable = hasRunDetail(run);
    const detailQuery = useQuery({
        queryKey: ['ledger-operation-detail', ledgerId, run.id],
        queryFn: () => fetchSyncRunDetail(ledgerId, run.id),
        enabled: expanded && expandable,
    });

    const line = (
        <>
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
                    <span className="font-medium">
                        {ledgerOperationLabel(run.providerKey)}
                        {connectionName ? (
                            <span className="font-normal text-text-muted"> · {connectionName}</span>
                        ) : null}
                    </span>
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
            {expandable ? (
                expanded ? (
                    <ChevronDown className="mt-1 size-icon-xs shrink-0 text-text-subtle" aria-hidden />
                ) : (
                    <ChevronRight className="mt-1 size-icon-xs shrink-0 text-text-subtle" aria-hidden />
                )
            ) : null}
        </>
    );

    return (
        <li className="text-sm">
            {expandable ? (
                <button
                    type="button"
                    onClick={() => setExpanded((v) => !v)}
                    aria-expanded={expanded}
                    className="flex w-full items-start gap-3 px-4 py-2.5 text-left hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
                >
                    {line}
                </button>
            ) : (
                <div className="flex items-start gap-3 px-4 py-2.5">{line}</div>
            )}
            {expanded && expandable ? (
                <div className="space-y-2 border-t border-border/60 bg-surface-muted/20 px-4 py-2.5 text-[0.8125rem]">
                    {detailQuery.isPending ? (
                        <p className="text-text-subtle">Loading…</p>
                    ) : detailQuery.isError ? (
                        <p role="alert" className="text-state-danger">
                            {errorMessage(detailQuery.error, 'Could not load the details.')}
                        </p>
                    ) : (
                        <RunDetail detail={detailQuery.data} />
                    )}
                </div>
            ) : null}
        </li>
    );
}

/** What a needs-reauth run means and what to do, since that run records no message of
 *  its own: the bank refused the stored access, and only an owner re-connecting fixes it. */
const NEEDS_REAUTH_LINE =
    'The bank refused this connection\'s stored access, so nothing was pulled. '
    + 'Re-connect it in Settings → Bank feeds.';

function RunDetail({ detail }: { detail: SyncRunDetail }) {
    const { errors, promotions } = detail;
    const failure = detail.summary.errorMessage
        ?? (detail.summary.status === 'needs_reauth' ? NEEDS_REAUTH_LINE : null);
    // Every pending charge that cleared is a promotion, whether or not its amount moved.
    // Only the ones that moved are "cleared at a different amount"; the rest are counted.
    const changed = promotions.filter((p) => p.wasAmount !== p.becameAmount);
    const sameAmount = promotions.length - changed.length;
    return (
        <>
            {failure ? (
                <p className="text-state-danger">{failure}</p>
            ) : null}
            {errors.length > 0 ? (
                <div>
                    <p className="font-medium text-text-muted">Errors:</p>
                    {errorGroups(errors).map((group) => (
                        <div key={group.source ?? ''}>
                            {group.source !== null ? (
                                <p className="mt-1.5 font-semibold text-text-muted">{errorSourceLabel(group.source)}</p>
                            ) : null}
                            <ul className="ml-4 list-disc">
                                {group.errors.map((e, idx) => (
                                    <li key={`${e.code}-${idx}`}>
                                        <span className="font-mono">{e.code}</span> — <ErrorText error={e} />
                                    </li>
                                ))}
                            </ul>
                        </div>
                    ))}
                </div>
            ) : null}
            {sameAmount > 0 ? (
                <p className="text-text-muted">
                    {sameAmount} pending charge{sameAmount === 1 ? '' : 's'} cleared at the same amount.
                </p>
            ) : null}
            {changed.length > 0 ? (
                <div>
                    <p className="font-medium text-text-muted">Cleared at different amounts:</p>
                    <ul className="ml-4 list-disc">
                        {changed.map((p) => (
                            <li key={p.headerId}>
                                Was <strong>{promotionFigure(p.wasAmount, p.currencyCode)}</strong>,{' '}
                                cleared as{' '}
                                <strong>{promotionFigure(p.becameAmount, p.currencyCode)}</strong>
                            </li>
                        ))}
                    </ul>
                </div>
            ) : null}
            {!failure && errors.length === 0 && promotions.length === 0 ? (
                <p className="text-text-subtle">No further details were recorded for this run.</p>
            ) : null}
        </>
    );
}

/**
 * A run's errors grouped by the source that raised them, in the order each source first
 * appears. Errors that name no source (a sync's, and any row written before sources were
 * recorded) form one group with no heading, so such a run reads as it always did.
 */
function errorGroups(errors: readonly SyncErrorDto[]): { source: string | null; errors: SyncErrorDto[] }[] {
    const groups = new Map<string | null, SyncErrorDto[]>();
    for (const e of errors) {
        const list = groups.get(e.providerKey);
        if (list) list.push(e);
        else groups.set(e.providerKey, [e]);
    }
    return [...groups].map(([source, list]) => ({ source, errors: list }));
}

/**
 * An error's message, led by the account it is about. The account is named by what the
 * server resolved, else by the feed's id for it, unless the message already carries that
 * id, as a sync's errors and older rows do.
 */
function ErrorText({ error }: { error: SyncErrorDto }) {
    const id = error.simpleFinAccountId;
    const account = error.accountLabel ?? (id !== null && !error.message.includes(id) ? id : null);
    return account === null ? (
        <>{error.message}</>
    ) : (
        <>
            <span className="font-semibold">{account}</span>: {error.message}
        </>
    );
}

/**
 * A promote-on-clear figure in the currency of the account the row lives on, at that
 * currency's places (ADR-0114 D15). A row with no originating account has no currency,
 * so its figure prints as stored, with no symbol: never one guessed for it.
 */
function promotionFigure(amount: number, currency: string | null): string {
    return currency === null ? formatAsSent(String(amount)) : formatCurrency(amount, currency);
}

