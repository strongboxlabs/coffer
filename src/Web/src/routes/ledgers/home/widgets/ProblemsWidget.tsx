import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { SkeletonRows } from '@/components/ui/Skeleton';
import { fetchCurrentUser } from '@/lib/api';
import { formatInstantDateCompact, formatLedgerDateTime } from '@/lib/dates';
import type { OutstandingProblem, Schedule } from '@/lib/types';
import { useLedgerAttention } from '@/lib/useLedgerAttention';

import { checkedPhrase, problemDestination, type ProblemDestination } from '../attention';
import { useHome, useWidgetFrame } from '../homeContext';
import { useConsistencyState, useOutstanding, useSchedules } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

const MAX_ROWS = 5;

/**
 * Unresolved warning-or-above subjects for this ledger, critical first (ADR-0102).
 * The count is the rail's own, so the two cannot disagree; the rows come from the
 * same outstanding-subjects query behind it. No dismiss: a problem leaves when its
 * condition resolves.
 */
export function ProblemsWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const attention = useLedgerAttention();
    const count = attention.countFor(home.ledgerId) ?? (attention.isPending ? undefined : 0);
    const outstanding = useOutstanding(home.ledgerId, count);
    const consistency = useConsistencyState(home.ledgerId);
    const schedules = useSchedules(home.ledgerId);
    const me = useQuery({ queryKey: ['me'], queryFn: fetchCurrentUser });

    const link = (
        <Link
            to="/ledgers/$ledgerId/settings"
            params={{ ledgerId: home.ledgerId }}
            search={{ tab: 'notifications' }}
            className="text-xs font-medium text-accent hover:underline"
        >
            All notices →
        </Link>
    );
    const title = count ? `Problems · ${count}` : 'Problems';

    if (attention.isError) {
        return (
            <WidgetPanel title="Problems" link={link}>
                <WidgetError what="problems" error={new Error('The attention check failed.')} onRetry={attention.refetch} />
            </WidgetPanel>
        );
    }
    if (count === undefined || (count > 0 && outstanding.isPending)) {
        return (
            <WidgetPanel title={title} link={link}>
                <SkeletonRows rows={1} label="Loading problems" rowClassName="h-fixed-40px" />
            </WidgetPanel>
        );
    }
    if (count === 0) {
        const collapse = frame.settings.collapseWhenClear !== false;
        return (
            <WidgetPanel title={title} link={link}>
                <p className={collapse ? 'px-4 py-2 text-xs text-text-muted' : 'p-4 text-sm text-text-muted'}>
                    No open problems · {checkedPhrase(consistency.data)}
                </p>
            </WidgetPanel>
        );
    }
    if (outstanding.isError) {
        return (
            <WidgetPanel title={title} link={link}>
                <WidgetError what="problems" error={outstanding.error} onRetry={() => void outstanding.refetch()} />
            </WidgetPanel>
        );
    }

    const rows = outstanding.data ?? [];
    return (
        <WidgetPanel title={title} link={link}>
            <ul className="divide-y divide-border/60">
                {rows.slice(0, MAX_ROWS).map((p) => (
                    <ProblemRow
                        key={p.subjectKey}
                        problem={p}
                        lastSuccessAt={lastSuccessFor(p, schedules.data)}
                        canEdit={home.canEdit}
                        isAdmin={me.data?.isAdmin === true}
                    />
                ))}
            </ul>
            {rows.length > MAX_ROWS ? (
                <p className="border-t border-border/60 px-4 py-2 text-xs text-text-muted">
                    +{rows.length - MAX_ROWS} more ·{' '}
                    <Link
                        to="/ledgers/$ledgerId/settings"
                        params={{ ledgerId: home.ledgerId }}
                        search={{ tab: 'notifications' }}
                        className="font-medium text-accent hover:underline"
                    >
                        All notices →
                    </Link>
                </p>
            ) : null}
        </WidgetPanel>
    );
}

/** The job behind a subject, when it is one: its last good run is the useful detail. */
function lastSuccessFor(p: OutstandingProblem, schedules: Record<string, Schedule> | undefined): string | null {
    const job = p.subjectKey.slice(0, -1);
    return schedules?.[job]?.lastSuccessAt ?? null;
}

function ProblemRow({ problem, lastSuccessAt, canEdit, isAdmin }: {
    problem: OutstandingProblem;
    lastSuccessAt: string | null;
    canEdit: boolean;
    isAdmin: boolean;
}) {
    const critical = problem.severity === 'critical';
    const destination = problemDestination(problem.subjectKey, problem.eventKey);
    return (
        <li className="flex flex-wrap items-start justify-between gap-x-3 gap-y-1 px-4 py-2 text-sm">
            <div className="min-w-0 flex-1">
                <p className="flex flex-wrap items-baseline gap-x-2">
                    <span className={critical ? 'text-xs font-medium text-state-danger' : 'text-xs font-medium text-state-warning'}>
                        {critical ? 'Critical' : 'Warning'}
                    </span>
                    <span className="min-w-0">
                        {problem.summary}
                        <span className="text-text-muted"> · since {formatInstantDateCompact(problem.firstUnresolvedAt)}</span>
                    </span>
                </p>
                {lastSuccessAt ? (
                    <p className="text-xs text-text-muted">last success {formatLedgerDateTime(lastSuccessAt)}</p>
                ) : null}
            </div>
            <DestinationLink destination={destination} canEdit={canEdit} isAdmin={isAdmin} />
        </li>
    );
}

/**
 * The link to the page that fixes a problem. A viewer can read the page but not act
 * on it, so every link reads "Details →" for them; the backups page is admin-only,
 * so for anyone else it is named but not linked.
 */
function DestinationLink({ destination, canEdit, isAdmin }: {
    destination: ProblemDestination;
    canEdit: boolean;
    isAdmin: boolean;
}) {
    const home = useHome();
    const className = 'shrink-0 text-xs font-medium text-accent hover:underline';
    if (destination.kind === 'reminders') {
        return (
            <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId: home.ledgerId }} className={className}>
                {canEdit ? 'Reminders →' : 'Details →'}
            </Link>
        );
    }
    if (destination.kind === 'system-backups') {
        return isAdmin
            ? <Link to="/system" search={{ tab: 'backups' }} className={className}>{canEdit ? 'Backups →' : 'Details →'}</Link>
            : <span className="shrink-0 text-xs text-text-muted">System → Backups</span>;
    }
    const label = !canEdit ? 'Details →'
        : destination.check ? 'Check and repair →'
        : destination.tab === 'feeds' ? 'Bank feeds →'
        : destination.tab === 'quotes' ? 'Quotes →'
        : destination.tab === 'snapshots' ? 'Snapshots →'
        : 'Notifications →';
    return (
        <Link
            to="/ledgers/$ledgerId/settings"
            params={{ ledgerId: home.ledgerId }}
            search={{
                ...(destination.tab === 'general' ? {} : { tab: destination.tab }),
                ...(destination.check && canEdit ? { check: true as const } : {}),
            }}
            className={className}
        >
            {label}
        </Link>
    );
}
