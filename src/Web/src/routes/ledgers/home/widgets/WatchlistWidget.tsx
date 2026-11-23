import { useMemo } from 'react';
import { Link } from '@tanstack/react-router';

import { Button } from '@/components/ui/Button';
import { MarkBar } from '@/components/ui/MarkBar';
import { SkeletonRows } from '@/components/ui/Skeleton';
import { buildAccountPathMap } from '@/lib/accountPath';
import { monthShortLabel } from '@/lib/calendar';
import { categoryDotClass, systemCategoryIds } from '@/lib/categoryChip';
import { cn } from '@/lib/cn';
import { atDisplayPrecision, formatCurrency } from '@/lib/money';
import type { AccountSummary, BudgetCategoryRow, BudgetProgress } from '@/lib/types';

import { markIsBudgeted, pastMark } from '../../budget/cumulative';
import { typicalBasis, usualSpan } from '../../budget/usualSpan';
import { useHome, useWidgetFrame } from '../homeContext';
import { NoRate } from '../Money';
import { useBudgetProgress, useRailAccounts } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

/** One watched category: its names, and this month's row when the month has one. */
interface Watched {
    id: string;
    /** The leaf name, which picks the identity dot as it does everywhere else. */
    name: string;
    /** `Parent/Child`, what the row prints. */
    path: string;
    /** Absent when the month reports nothing for it: no spending, history or target. */
    row: BudgetCategoryRow | undefined;
    /** Its mark rests on a target, its own or one typed beneath it. */
    budgeted: boolean;
}

/**
 * Chosen categories against their marks this month (ADR-0099), each a bar to its
 * mark with the figures and what the mark is. Its months are This month's, from
 * the same read. Two states only, under or over, and it never notifies: over is
 * plain text inside the widget.
 *
 * Figures are in the ledger's reporting currency, each converted (ADR-0114 D7). A
 * spend that needs a missing rate reads "no rate" beside its mark, with no bar
 * (ADR-0114 D9): it is neither "nothing spent yet" nor a figure to be over by.
 */
export function WatchlistWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const chosen = Array.isArray(frame.settings.categoryIds) ? (frame.settings.categoryIds as string[]) : [];
    const sort = frame.settings.sort === 'most-over' ? 'most-over' : 'chosen';
    const progress = useBudgetProgress(home.ledgerId, home.today, home.budgetWindowMonths);
    const accounts = useRailAccounts(home.ledgerId);
    const paths = useMemo(() => buildAccountPathMap(accounts.data ?? []), [accounts.data]);
    // The ledger's Uncategorized, whose dot is always neutral.
    const systemIds = useMemo(() => systemCategoryIds(accounts.data), [accounts.data]);

    const [year, month] = home.today.split('-').map(Number);
    const title = <>Watchlist <span className="font-normal text-text-muted">· {monthShortLabel(year!, month!)}</span></>;
    const link = (
        <Link to="/ledgers/$ledgerId/budget" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Budget →
        </Link>
    );

    if (chosen.length === 0) return <WidgetPanel title={title} link={link}><NoneChosen /></WidgetPanel>;
    if (progress.isError || accounts.isError) {
        return (
            <WidgetPanel title={title} link={link}>
                <WidgetError
                    what="the watchlist"
                    error={progress.error ?? accounts.error}
                    onRetry={() => { void progress.refetch(); void accounts.refetch(); }}
                />
            </WidgetPanel>
        );
    }
    if (!progress.data || !accounts.data) {
        return <WidgetPanel title={title} link={link}><SkeletonRows rows={chosen.length} label="Loading the watchlist" /></WidgetPanel>;
    }

    const watched = watchedCategories(chosen, progress.data, accounts.data, paths);
    if (watched.length === 0) return <WidgetPanel title={title} link={link}><NoneChosen /></WidgetPanel>;
    const rows = sort === 'most-over' ? mostOverFirst(watched, progress.data.currencyCode) : watched;

    // One scale for every bar, over both sides, as the Budget table's: bar length
    // means money, so rows compare, and a category far under its mark still shows
    // its tick inside the track.
    const scaleMax = Math.max(...rows.map((w) => Math.max(w.row?.actual ?? 0, w.row?.mark ?? 0)), 1) * 1.05;

    return (
        <WidgetPanel title={title} link={link}>
            <ul aria-label="Watched categories" className="flex flex-col gap-3 px-4 pb-4 pt-3">
                {rows.map((w) => (
                    <li key={w.id}>
                        <WatchRow watched={w} data={progress.data} scaleMax={scaleMax} systemIds={systemIds} />
                    </li>
                ))}
            </ul>
        </WidgetPanel>
    );
}

function NoneChosen() {
    const home = useHome();
    const frame = useWidgetFrame();
    return (
        <p className="flex flex-wrap items-center gap-x-1.5 p-4 text-sm text-text-muted">
            <span>No categories chosen</span>
            <span aria-hidden>·</span>
            <Button variant="link" size="xs" onClick={frame.openSettings} disabled={!home.layoutWritable}>Settings…</Button>
        </p>
    );
}

function WatchRow({ watched, data, scaleMax, systemIds }: {
    watched: Watched;
    data: BudgetProgress;
    scaleMax: number;
    /** The ledger's system category ids (its Uncategorized): a neutral dot. */
    systemIds: ReadonlySet<string>;
}) {
    const home = useHome();
    const { row } = watched;
    const currency = data.currencyCode;
    // A spend that needs a missing rate is no figure (ADR-0114 D9): it reads "no rate",
    // never "nothing spent yet", and no bar, over or distance is drawn from it.
    const noRate = row !== undefined && row.actual === null;
    // A mark that needs a missing rate (a target or a usual that could not convert) is
    // not a category with no history: it reads "no rate", never "new".
    const markNoRate = row !== undefined && row.mark === null && (row.rateUnavailable?.length ?? 0) > 0;
    // As the figures print, so the bar, the words and the figures agree.
    const actual = atDisplayPrecision(row?.actual ?? 0, currency);
    const mark = row === undefined || row.mark === null ? null : atDisplayPrecision(row.mark, currency);
    const past = row === undefined ? null : pastMark(row, currency);
    const over = past !== null && past > 0;

    const name = (
        <span className="flex min-w-0 items-center gap-1.5 text-sm" title={watched.path}>
            <span aria-hidden className={cn('size-dot-sm shrink-0 rounded-full', categoryDotClass(watched.name, watched.id, systemIds.has(watched.id)))} />
            <span className="truncate group-hover:underline">{watched.path}</span>
        </span>
    );

    return (
        <Link
            to="/ledgers/$ledgerId/accounts/$accountId"
            params={{ ledgerId: home.ledgerId, accountId: watched.id }}
            search={{ subcategories: true }}
            className="group flex flex-col gap-0.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2"
        >
            {noRate ? (
                <span className="flex items-baseline justify-between gap-2">
                    {name}
                    <span className="shrink-0 text-xs">
                        <NoRate missing={row.rateUnavailable ?? []} />{' '}
                        {mark === null ? null : (
                            <span className="font-mono font-medium tabular-nums text-text-muted">of {formatCurrency(mark, currency)}</span>
                        )}
                    </span>
                </span>
            ) : actual === 0 ? (
                <span className="flex items-baseline justify-between gap-2">
                    {name}
                    <span className="shrink-0 text-xs text-text-muted">nothing spent yet</span>
                </span>
            ) : (
                <>
                    <span className="flex items-baseline justify-between gap-2">
                        {name}
                        <span className="shrink-0 text-xs">
                            <span className="font-mono font-semibold tabular-nums text-text">{formatCurrency(actual, currency)}</span>{' '}
                            {mark === null && markNoRate ? (
                                <NoRate missing={row.rateUnavailable ?? []} />
                            ) : mark === null ? (
                                <span className="text-text-muted">new</span>
                            ) : (
                                <span className="font-mono font-medium tabular-nums text-text-muted">of {formatCurrency(mark, currency)}</span>
                            )}
                        </span>
                    </span>
                    <MarkBar actual={actual} mark={mark} scaleMax={scaleMax} />
                    {mark !== null ? (
                        <span className="text-[0.6875rem] text-text-muted">
                            {watched.budgeted ? 'budgeted' : typicalBasis(usualSpan(data))}
                            {over ? (
                                <>
                                    {' · '}
                                    <span className="font-mono font-medium tabular-nums text-state-danger">
                                        +{formatCurrency(past, currency)} over
                                    </span>
                                </>
                            ) : null}
                        </span>
                    ) : null}
                </>
            )}
        </Link>
    );
}

/**
 * The chosen ids that still name a category, in the order chosen. An id that names
 * none (a category since deleted) is left out here, where the accounts are known;
 * the layout keeps it until the list is next edited.
 */
function watchedCategories(
    chosen: readonly string[],
    data: BudgetProgress,
    accounts: readonly AccountSummary[],
    paths: ReadonlyMap<string, string>,
): Watched[] {
    const categories = new Map(accounts.filter((a) => a.accountType === 'category').map((a) => [a.id, a]));
    const rows = new Map(data.rows.map((r) => [r.categoryId, r]));
    // The whole tree, so a target typed beneath an intermediate category the month
    // reports nothing for still reaches the watched one above it.
    const parentOf = (id: string) => categories.get(id)?.parentId ?? null;
    return chosen.flatMap((id) => {
        const category = categories.get(id);
        if (!category) return [];
        return [{
            id,
            name: category.name,
            path: paths.get(id) ?? category.name,
            row: rows.get(id),
            budgeted: markIsBudgeted(data.rows, id, parentOf),
        }];
    });
}

/** Most over first, by how far past its mark; a category with no mark has no
 *  distance and goes last, in the order chosen. */
function mostOverFirst(watched: readonly Watched[], currency: string): Watched[] {
    const by = (w: Watched) => (w.row === undefined ? null : pastMark(w.row, currency));
    return [...watched].sort((a, b) => {
        const x = by(a);
        const y = by(b);
        if (x === null || y === null) return x === null ? (y === null ? 0 : 1) : -1;
        return y - x;
    });
}
