import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link } from '@tanstack/react-router';

import { Button } from '@/components/ui/Button';
import { SkeletonRows } from '@/components/ui/Skeleton';
import { fetchSecurities, fetchSnapshots, syncFeedConnection } from '@/lib/api';
import { formatLedgerDateCompact, formatLedgerDateTime } from '@/lib/dates';
import { formatRelative } from '@/lib/ledgerOperationDisplay';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import type { FeedConnectionSummary } from '@/lib/types';

import { useHome } from '../homeContext';
import { useConsistencyState, useFeedConnections, useSchedules } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

/** A feed's status as a word. Only a status that is not ok takes a state colour. */
const STATUS: Record<string, { word: string; tone: string }> = {
    active: { word: 'ok', tone: 'text-text-muted' },
    needs_reauth: { word: 'needs re-auth', tone: 'text-state-warning' },
    error: { word: 'error', tone: 'text-state-danger' },
    disconnected: { word: 'disconnected', tone: 'text-state-danger' },
};

/**
 * Facts with timestamps, and no dots: each bank feed, then prices, auto-post,
 * backups and the consistency check, each with when it last ran. A status word takes
 * a state colour only when it is not ok, and nothing here judges an age by a
 * threshold of its own: a stale feed is a problem the feed itself reports.
 */
export function FeedsChecksWidget() {
    const home = useHome();
    const feeds = useFeedConnections(home.ledgerId);
    const schedules = useSchedules(home.ledgerId);
    const consistency = useConsistencyState(home.ledgerId);
    const snapshots = useQuery({ queryKey: ['snapshots', home.ledgerId], queryFn: () => fetchSnapshots(home.ledgerId) });
    const securities = useQuery({ queryKey: ['securities', home.ledgerId], queryFn: () => fetchSecurities(home.ledgerId) });

    const link = (
        <Link to="/ledgers/$ledgerId/settings" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Settings →
        </Link>
    );
    const failed = [feeds, schedules, consistency, snapshots, securities].find((q) => q.isError);
    if (failed) {
        return (
            <WidgetPanel title="Feeds & checks" link={link}>
                <WidgetError
                    what="feeds and checks"
                    error={failed.error}
                    onRetry={() => [feeds, schedules, consistency, snapshots, securities].forEach((q) => void q.refetch())}
                />
            </WidgetPanel>
        );
    }
    if (!feeds.data || !schedules.data || !consistency.data || !snapshots.data || !securities.data) {
        return <WidgetPanel title="Feeds & checks" link={link}><SkeletonRows rows={6} label="Loading feeds and checks" /></WidgetPanel>;
    }

    const quotes = schedules.data['quote-refresh'];
    const autoPost = schedules.data['reminder-auto-post'];
    const held = securities.data.filter((s) => s.totalQuantity !== 0 && s.latestPriceAsOf !== null);
    const oldestPrice = held.length === 0 ? null : held.map((s) => s.latestPriceAsOf!).sort()[0]!;
    const lastSnapshot = snapshots.data[0]?.createdAt ?? null;
    const state = consistency.data;
    const connections = feeds.data;

    return (
        <WidgetPanel title="Feeds & checks" link={link}>
            <ul className="divide-y divide-border/60 text-sm">
                {connections.length === 0 ? (
                    <li className="px-4 py-2 text-text-muted">
                        No bank feeds connected
                        {home.isOwner ? (
                            <>
                                {' · '}
                                <Link to="/ledgers/$ledgerId/settings" params={{ ledgerId: home.ledgerId }} search={{ tab: 'feeds' }} className="font-medium text-accent hover:underline">
                                    Connect →
                                </Link>
                            </>
                        ) : null}
                    </li>
                ) : connections.map((f) => <FeedRow key={f.id} feed={f} />)}
                <FactRow
                    name="Prices"
                    to={{ kind: 'settings', tab: 'quotes' }}
                    fact={quotes?.lastSuccessAt ? `refreshed ${formatRelative(quotes.lastSuccessAt)}` : 'not refreshed yet'}
                    detail={oldestPrice ? `oldest price used ${formatLedgerDateCompact(oldestPrice)}` : null}
                />
                <FactRow
                    name="Auto-post"
                    to={{ kind: 'reminders' }}
                    fact={!autoPost?.enabled ? 'off' : autoPost.lastRunAt ? `ran ${formatRelative(autoPost.lastRunAt)}` : 'not run yet'}
                />
                <FactRow
                    name="Backup"
                    to={{ kind: 'settings', tab: 'snapshots' }}
                    fact={lastSnapshot ? `last snapshot ${formatRelative(lastSnapshot)}` : 'no snapshot yet'}
                />
                <FactRow
                    name="Checks"
                    to={{ kind: 'settings', tab: 'general' }}
                    fact={!state?.lastCheckedAt || state.healthy === null ? 'not checked yet'
                        : state.healthy ? `consistent · ${formatRelative(state.lastCheckedAt)}`
                        : `${state.failingProjections.length} disagree · ${formatRelative(state.lastCheckedAt)}`}
                    tone={state?.healthy === false ? 'text-state-warning' : undefined}
                />
            </ul>
        </WidgetPanel>
    );
}

function FeedRow({ feed }: { feed: FeedConnectionSummary }) {
    const home = useHome();
    const queryClient = useQueryClient();
    const status = STATUS[feed.status] ?? { word: feed.status, tone: 'text-state-warning' };
    const sync = useMutation({
        mutationFn: () => syncFeedConnection(home.ledgerId, feed.id),
        onSuccess: () => {
            invalidateLedgerRegister(queryClient, home.ledgerId);
            void queryClient.invalidateQueries({ queryKey: ['feed-connections', home.ledgerId] });
            void queryClient.invalidateQueries({ queryKey: ['ledger-operations', home.ledgerId] });
        },
    });
    const name = feed.institutionName ?? 'SimpleFIN';
    const lastSynced = feed.lastSyncedAt ? formatRelative(feed.lastSyncedAt) : 'never synced';

    return (
        <li className="flex flex-wrap items-center gap-x-2 gap-y-1 px-4 py-1.5">
            <Link
                to="/ledgers/$ledgerId/settings"
                params={{ ledgerId: home.ledgerId }}
                search={{ tab: 'feeds' }}
                className="min-w-0 flex-1 truncate hover:underline"
                title={feed.lastSyncedAt ? `${name} · last synced ${formatLedgerDateTime(feed.lastSyncedAt)}` : name}
            >
                {name}
            </Link>
            <span className={status.tone}>{status.word}</span>
            <span className="text-xs text-text-muted">{lastSynced}</span>
            {home.canEdit && feed.status === 'active' ? (
                <Button variant="link" size="xs" pending={sync.isPending} pendingLabel="Syncing…" onClick={() => sync.mutate()}>
                    Sync now
                </Button>
            ) : null}
            {home.isOwner && feed.status === 'needs_reauth' ? (
                <Link to="/ledgers/$ledgerId/settings" params={{ ledgerId: home.ledgerId }} search={{ tab: 'feeds' }} className="text-xs font-medium text-accent hover:underline">
                    Reconnect →
                </Link>
            ) : null}
        </li>
    );
}

type FactDestination = { kind: 'settings'; tab: 'quotes' | 'snapshots' | 'general' } | { kind: 'reminders' };

function FactRow({ name, to, fact, detail, tone }: {
    name: string;
    to: FactDestination;
    fact: string;
    detail?: string | null;
    tone?: string;
}) {
    const home = useHome();
    const nameClass = 'w-fixed-80px shrink-0 hover:underline';
    return (
        <li className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 px-4 py-1.5">
            {to.kind === 'reminders' ? (
                <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId: home.ledgerId }} className={nameClass}>{name}</Link>
            ) : (
                <Link
                    to="/ledgers/$ledgerId/settings"
                    params={{ ledgerId: home.ledgerId }}
                    search={to.tab === 'general' ? {} : { tab: to.tab }}
                    className={nameClass}
                >
                    {name}
                </Link>
            )}
            {/* One run of text, so a detail that wraps flows on after the fact
                rather than starting a line with its separator. */}
            <span className="min-w-0 flex-1">
                <span className={tone ?? 'text-text-muted'}>{fact}</span>
                {detail ? <span className="text-xs text-text-muted"> · {detail}</span> : null}
            </span>
        </li>
    );
}
