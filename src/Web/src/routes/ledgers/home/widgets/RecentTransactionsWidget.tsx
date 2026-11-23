import { useMemo } from 'react';
import { Link } from '@tanstack/react-router';

import { SkeletonRows } from '@/components/ui/Skeleton';
import { StatusBadge } from '@/components/ui/StatusBadge';
import { TagColorsProvider } from '@/components/tags/TagColorsContext';
import type { EntriesDirection } from '@/lib/api';
import { buildAccountPathMap } from '@/lib/accountPath';
import { systemCategoryIds } from '@/lib/categoryChip';
import { cn } from '@/lib/cn';
import { formatLedgerDateCompact } from '@/lib/dates';
import type { LedgerEntry } from '@/lib/types';

import { registerRowChrome } from '../../register/shell/registerRowChrome';
import { resolveRowStatus } from '../../register/shell/registerStatus';
import { EntryAmount, EntryCategory } from '../EntryCells';
import { useHome, useWidgetFrame } from '../homeContext';
import { useRailAccounts, useRecentEntries } from '../queries';
import {
    ENTRY_ACCOUNT,
    ENTRY_AMOUNT,
    ENTRY_BREAK,
    ENTRY_CATEGORY,
    ENTRY_INDENT,
    ENTRY_LEAD,
    ENTRY_PAYEE,
    entryKey,
    shownRow,
} from '../rows';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

const HEAD: Record<EntriesDirection, string> = { all: 'all accounts', outflow: 'outflows', inflow: 'inflows' };

// The entry row's columns (../rows), led by the status badge. Focus is an outline,
// not the usual ring: a ring is a box-shadow, and a row awaiting review sets its own
// inline box-shadow for the warning bar, which would replace it.
const ROW ='group flex min-h-control-36px flex-wrap items-center gap-x-3 gap-y-1 px-4 py-1 text-sm '
    + 'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent';

/**
 * The latest register entries across every account, newest first, each drawn as its
 * own account's register draws it: the status badge, the category cell and the
 * amount in that account's currency. A row opens its register at the entry; nothing
 * is edited here, so the whole row is the link and holds no control of its own. A
 * row awaiting review carries the register's needs-review chrome.
 */
export function RecentTransactionsWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const rows = Number(frame.settings.rows ?? 10);
    const direction = (String(frame.settings.direction ?? 'all')) as EntriesDirection;
    const entries = useRecentEntries(home.ledgerId, home.today, rows, direction);
    const accounts = useRailAccounts(home.ledgerId);
    const names = useMemo(() => new Map((accounts.data ?? []).map((a) => [a.id, a.name])), [accounts.data]);
    const paths = useMemo(() => buildAccountPathMap(accounts.data ?? []), [accounts.data]);
    // The ledger's Uncategorized, whose chip is always neutral, as in the register.
    const systemIds = useMemo(() => systemCategoryIds(accounts.data), [accounts.data]);

    const registers = (
        <Link to="/ledgers/$ledgerId/accounts" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Registers →
        </Link>
    );
    const title = <>Recent transactions <span className="font-normal text-text-muted">· {HEAD[direction] ?? HEAD.all}</span></>;

    if (entries.isError) {
        return (
            <WidgetPanel title={title} link={registers}>
                <WidgetError what="recent transactions" error={entries.error} onRetry={() => void entries.refetch()} />
            </WidgetPanel>
        );
    }
    if (!entries.data) {
        return <WidgetPanel title={title} link={registers}><SkeletonRows rows={rows} label="Loading recent transactions" /></WidgetPanel>;
    }

    return (
        <WidgetPanel title={title} link={registers}>
            {entries.data.entries.length === 0 ? (
                <p className="p-4 text-sm text-text-muted">
                    No transactions yet ·{' '}
                    <Link to="/ledgers/$ledgerId/accounts" params={{ ledgerId: home.ledgerId }} className="font-medium text-accent hover:underline">
                        Registers →
                    </Link>
                </p>
            ) : (
                <TagColorsProvider ledgerId={home.ledgerId}>
                    <ul aria-label="Recent transactions" className="divide-y divide-border/60">
                        {entries.data.entries.map((item) => (
                            <EntryRow
                                key={entryKey(item)}
                                item={item}
                                accountName={names.get(item.accountId)}
                                paths={paths}
                                systemIds={systemIds}
                            />
                        ))}
                    </ul>
                </TagColorsProvider>
            )}
        </WidgetPanel>
    );
}

function EntryRow({ item, accountName, paths, systemIds }: {
    item: LedgerEntry;
    accountName: string | undefined;
    paths: ReadonlyMap<string, string>;
    /** The ledger's system category ids (its Uncategorized): a neutral chip. */
    systemIds: ReadonlySet<string>;
}) {
    const home = useHome();
    const row = shownRow(item.entry);
    const status = resolveRowStatus(row, localDate(home.today));
    const chrome = registerRowChrome({ focused: false, needsReview: row.needsReview });

    return (
        <li>
            <Link
                to="/ledgers/$ledgerId/accounts/$accountId"
                params={{ ledgerId: home.ledgerId, accountId: item.accountId }}
                search={{ focus: row.headerId }}
                className={cn(ROW, chrome.bgClass)}
                style={chrome.boxShadow ? { boxShadow: chrome.boxShadow } : undefined}
                title={row.needsReview ? 'Needs review' : undefined}
            >
                <span className={ENTRY_LEAD}>
                    <StatusBadge status={status} className="shrink-0" />
                    <span className="font-mono text-xs tabular-nums text-text-subtle">{formatLedgerDateCompact(row.postedAt)}</span>
                </span>
                {row.needsReview ? <span className="sr-only">Needs review</span> : null}
                <span className={ENTRY_PAYEE} title={row.payee ?? undefined}>
                    {row.payee ?? <span className="text-text-subtle">—</span>}
                </span>
                <span aria-hidden className={ENTRY_BREAK} />
                <span aria-hidden className={ENTRY_INDENT} />
                <span className={ENTRY_ACCOUNT} title={accountName}>{accountName ?? '—'}</span>
                <span className={ENTRY_CATEGORY}><EntryCategory item={item} paths={paths} systemIds={systemIds} /></span>
                <span className={ENTRY_AMOUNT}><EntryAmount item={item} /></span>
            </Link>
        </li>
    );
}

/** `YYYY-MM-DD` as a local date, the form the register's status rule reads. */
function localDate(day: string): Date {
    const [y, m, d] = day.split('-').map(Number);
    return new Date(y!, m! - 1, d!);
}
