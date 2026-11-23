import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { SkeletonRows } from '@/components/ui/Skeleton';
import { fetchLedgerOperations } from '@/lib/api';
import {
    familyClass,
    formatRelative,
    ledgerOperationLabel,
    statusClass,
    summarizeLedgerOperation,
} from '@/lib/ledgerOperationDisplay';

import { useHome, useWidgetFrame } from '../homeContext';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

/**
 * The latest imports, syncs and price refreshes. Read-only, like the Activity panel
 * it summarises: a log reports what ran and does not act on it.
 */
export function ActivityWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const rows = Number(frame.settings.rows ?? 5);
    const families = Array.isArray(frame.settings.families) ? (frame.settings.families as string[]) : ['ingest', 'quote'];
    // Asked for a little more than shown, so a family filter still fills the list.
    const query = useQuery({
        queryKey: ['home', home.ledgerId, 'activity', rows],
        queryFn: () => fetchLedgerOperations(home.ledgerId, { days: 30, limit: rows * 2 }),
    });

    const link = (
        <Link
            to="/ledgers/$ledgerId/settings"
            params={{ ledgerId: home.ledgerId }}
            search={{ tab: 'activity' }}
            className="text-xs font-medium text-accent hover:underline"
        >
            Activity →
        </Link>
    );
    if (query.isError) {
        return <WidgetPanel title="Imports & refreshes" link={link}><WidgetError what="activity" error={query.error} onRetry={() => void query.refetch()} /></WidgetPanel>;
    }
    if (!query.data) {
        return <WidgetPanel title="Imports & refreshes" link={link}><SkeletonRows rows={rows} label="Loading activity" /></WidgetPanel>;
    }

    const items = query.data.filter((r) => families.includes(r.family)).slice(0, rows);
    return (
        <WidgetPanel title="Imports & refreshes" link={link}>
            {items.length === 0 ? (
                <p className="p-4 text-sm text-text-muted">No imports or refreshes in the last 30 days.</p>
            ) : (
                <ul className="divide-y divide-border/60">
                    {items.map((run) => (
                        <li key={run.id} className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1 px-4 py-1.5 text-sm">
                            <span className="flex min-w-0 items-center gap-2">
                                <span className={`rounded px-1.5 py-0.5 text-[0.625rem] font-semibold uppercase tracking-wider ${familyClass(run.family)}`}>
                                    {run.family}
                                </span>
                                <span className="truncate">{ledgerOperationLabel(run.providerKey)}</span>
                            </span>
                            <span className="flex shrink-0 items-center gap-2 text-xs text-text-muted">
                                <span className="font-mono tabular-nums">{summarizeLedgerOperation(run)}</span>
                                <span className={`rounded-full px-1.5 py-0.5 text-[0.5625rem] font-semibold uppercase tracking-wider ${statusClass(run.status)}`}>
                                    {run.status}
                                </span>
                                <span className="text-text-subtle">{formatRelative(run.startedAt)}</span>
                            </span>
                        </li>
                    ))}
                </ul>
            )}
        </WidgetPanel>
    );
}
