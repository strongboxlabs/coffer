import { useEffect, useMemo, useState } from 'react';
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';
import { Search } from 'lucide-react';

import { fetchAccounts, fetchTags, fetchTagTransactions } from '@/lib/api';
import type { TagDto, TagTransactionLine } from '@/lib/types';
import { formatLedgerDate } from '@/lib/dates';
import { errorMessage } from '@/lib/errorMessage';
import { formatCurrency } from '@/lib/money';
import { cn } from '@/lib/cn';
import { invalidateHomeReads } from '@/lib/registerInvalidation';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { EmptyState } from '@/components/ui/EmptyState';
import { Input } from '@/components/ui/Input';
import { Panel } from '@/components/ui/Panel';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';
import { MergeTagDialog, TagEditDialog } from '../settings/TagDialogs';
import { mergeSummaryText } from '../settings/mergeSummary';

import { rowLinkClass, transactionCountLabel } from './rowStyles';
import { ListError, ListLoading } from './StructureStates';

/**
 * `/ledgers/:ledgerId/tags/:tagId` — what one tag labels, across every account (ADR-0111 D6).
 * Opened from a tag's name on the Tags tab.
 *
 * The rows are register lines, newest first, each read from its own side as its account's register
 * shows it: a transfer tagged on both sides is a line in each, and one tagged on a single side is
 * a line on that side. A payee opens that transaction in its account's register, scrolled to it.
 * Read-only: changing a transaction is the register's job.
 */
const PAGE_SIZE = 50;

/** A value that follows another after it has stopped changing, so a search box is not a request per key. */
function useDebounced<T>(value: T, ms: number): T {
    const [settled, setSettled] = useState(value);
    useEffect(() => {
        const id = setTimeout(() => setSettled(value), ms);
        return () => clearTimeout(id);
    }, [value, ms]);
    return settled;
}

export function TaggedTransactionsPage() {
    const { ledgerId, tagId } = useParams({ strict: false }) as { ledgerId: string; tagId: string };
    const queryClient = useQueryClient();
    const { ledger, canEdit } = useLedgerRole(ledgerId);

    const tagsQuery = useQuery({ queryKey: ['tags', ledgerId], queryFn: () => fetchTags(ledgerId) });
    const tags = tagsQuery.data;
    const tag = tags?.find((t) => t.id === tagId);

    // The accounts the list can be narrowed to: the real ones, not the categories the read also carries.
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: false }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: false }),
    });
    const accountOptions = useMemo(
        () => (accountsQuery.data ?? []).filter((a) => a.accountType !== 'category'),
        [accountsQuery.data],
    );

    const [search, setSearch] = useState('');
    const [accountId, setAccountId] = useState('');
    const [dateFrom, setDateFrom] = useState('');
    const [dateTo, setDateTo] = useState('');
    const settledSearch = useDebounced(search.trim(), 300);
    const filter = useMemo(
        () => ({
            accountId: accountId || undefined,
            search: settledSearch || undefined,
            dateFrom: dateFrom || undefined,
            dateTo: dateTo || undefined,
        }),
        [accountId, settledSearch, dateFrom, dateTo],
    );
    const filtered = Boolean(filter.accountId || filter.search || filter.dateFrom || filter.dateTo);

    const linesQuery = useInfiniteQuery({
        queryKey: ['tag-transactions', ledgerId, tagId, filter],
        initialPageParam: 0,
        queryFn: ({ pageParam }) =>
            fetchTagTransactions(ledgerId, tagId, filter, { limit: PAGE_SIZE, offset: pageParam }),
        getNextPageParam: (last) => (last.hasMore ? last.offset + last.limit : undefined),
        // The tag may have been deleted; a request for it then answers "not found", which is
        // the page's own message, not something to retry.
        retry: false,
    });
    const lines = useMemo(() => linesQuery.data?.pages.flatMap((p) => p.lines) ?? [], [linesQuery.data]);

    type Dialog = { kind: 'edit' } | { kind: 'merge'; presetTargetId?: string | null };
    const [dialog, setDialog] = useState<Dialog | null>(null);
    const [mergeNotice, setMergeNotice] = useState<string | null>(null);

    const onSaved = () => {
        void queryClient.invalidateQueries({ queryKey: ['tags', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['register', ledgerId] });
        // Every OTHER tag's page names this tag among a line's other tags, so a rename must reach
        // them. This page's own lines do not change when its tag is renamed or recoloured, and
        // after a merge they are of a tag that no longer exists, which a refetch would only
        // answer with an error before the page learns the tag is gone.
        void queryClient.invalidateQueries({
            queryKey: ['tag-transactions', ledgerId],
            predicate: (query) => query.queryKey[2] !== tagId,
        });
        // The home screen's recent and to-review rows draw each row's tags by name and colour.
        invalidateHomeReads(queryClient, ledgerId);
    };

    const clearFilters = () => {
        setSearch('');
        setAccountId('');
        setDateFrom('');
        setDateTo('');
    };

    const gone = tags !== undefined && tag === undefined;
    // The tag's own read failed and there is nothing cached to show it from. Without this the
    // page would say "Loading…" for good, and its only retry would be for the lines.
    const tagFailed = tagsQuery.isError && tag === undefined;

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: ledger?.name ?? 'Ledger',
                            node: ledger ? (
                                <Link to="/ledgers/$ledgerId" params={{ ledgerId }} className="hover:text-text">
                                    {ledger.name}
                                </Link>
                            ) : 'Ledger',
                        },
                        {
                            label: 'Structure',
                            node: (
                                <Link to="/ledgers/$ledgerId/accounts" params={{ ledgerId }} className="hover:text-text">
                                    Structure
                                </Link>
                            ),
                        },
                        {
                            label: 'Tags',
                            node: (
                                <Link to="/ledgers/$ledgerId/tags" params={{ ledgerId }} className="hover:text-text">
                                    Tags
                                </Link>
                            ),
                        },
                        { label: tag?.name ?? 'Tag' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    {/* What a merge did, said where the reader is. The tag it was merged away is
                        gone as soon as the list refreshes, and this page is the one that has it. */}
                    {mergeNotice !== null ? (
                        <p role="status" className="rounded border border-border bg-surface-muted/40 px-2 py-1 text-xs text-text-muted">
                            {mergeNotice}
                        </p>
                    ) : null}
                    {gone ? (
                        <>
                            {/* The page has no heading of its own otherwise, outside the Structure layout. */}
                            <h1 className="sr-only">Tag not found</h1>
                            <EmptyState
                                message="That tag no longer exists"
                                hint="It was deleted or merged into another tag."
                                action={(
                                    <Link
                                        to="/ledgers/$ledgerId/tags"
                                        params={{ ledgerId }}
                                        className="text-sm text-accent underline-offset-2 hover:underline"
                                    >
                                        Back to Tags
                                    </Link>
                                )}
                            />
                        </>
                    ) : (
                        <>
                            <header className="space-y-1">
                                <div className="flex flex-wrap items-center justify-between gap-3">
                                    <h1 className="flex min-w-0 items-center gap-2 text-xl font-semibold tracking-tight">
                                        {tag ? (
                                            <span
                                                aria-hidden
                                                className={cn(
                                                    'size-dot-lg shrink-0 rounded-full border border-border/50',
                                                    tag.color ? '' : 'bg-surface-hover',
                                                )}
                                                style={tag.color ? { backgroundColor: tag.color } : undefined}
                                            />
                                        ) : null}
                                        <span className="truncate">{tag?.name ?? 'Tag'}</span>
                                    </h1>
                                    {canEdit && tag ? (
                                        <Button type="button" variant="secondary" size="sm" onClick={() => setDialog({ kind: 'edit' })}>
                                            Edit tag…
                                        </Button>
                                    ) : null}
                                </div>
                                {tagFailed ? null : (
                                    <p className="text-sm text-text-muted">
                                        {tag === undefined
                                            ? 'Loading…'
                                            : tag.transactionCount === 0
                                                ? 'No transaction carries this tag yet.'
                                                : `${transactionCountLabel(tag.transactionCount)} ${tag.transactionCount === 1 ? 'carries' : 'carry'} this tag.`}
                                    </p>
                                )}
                            </header>

                            {tagFailed ? (
                                <ListError
                                    message={errorMessage(tagsQuery.error, 'Could not load this tag.')}
                                    onRetry={() => void tagsQuery.refetch()}
                                />
                            ) : null}

                            <div className="flex flex-wrap items-center gap-3">
                                <div className="relative w-fixed-288px max-w-full">
                                    <Search
                                        aria-hidden
                                        className="pointer-events-none absolute left-2.5 top-1/2 size-icon-sm -translate-y-1/2 text-text-subtle"
                                    />
                                    <Input
                                        type="search"
                                        aria-label="Search payee or memo"
                                        placeholder="Search payee or memo"
                                        value={search}
                                        onChange={(e) => setSearch(e.target.value)}
                                        className="h-control-36px pl-8"
                                    />
                                </div>
                                <select
                                    aria-label="Account"
                                    value={accountId}
                                    onChange={(e) => setAccountId(e.target.value)}
                                    className="h-control-36px rounded-md border border-border bg-surface px-2 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                >
                                    <option value="">All accounts</option>
                                    {accountOptions.map((a) => (
                                        <option key={a.id} value={a.id}>{a.name}</option>
                                    ))}
                                </select>
                                <label className="flex items-center gap-1.5 text-xs text-text-muted">
                                    From
                                    <input
                                        type="date"
                                        value={dateFrom}
                                        max={dateTo || undefined}
                                        onChange={(e) => setDateFrom(e.target.value)}
                                        className="h-control-36px rounded-md border border-border bg-surface px-2 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                    />
                                </label>
                                <label className="flex items-center gap-1.5 text-xs text-text-muted">
                                    To
                                    <input
                                        type="date"
                                        value={dateTo}
                                        min={dateFrom || undefined}
                                        onChange={(e) => setDateTo(e.target.value)}
                                        className="h-control-36px rounded-md border border-border bg-surface px-2 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                    />
                                </label>
                            </div>

                            {linesQuery.isError ? (
                                <ListError
                                    message={errorMessage(linesQuery.error, 'Could not load this tag’s transactions.')}
                                    onRetry={() => void linesQuery.refetch()}
                                />
                            ) : linesQuery.isPending ? (
                                <ListLoading noun="transactions" />
                            ) : lines.length === 0 ? (
                                <EmptyState
                                    message={filtered ? 'No transaction matches' : 'Nothing is tagged with this yet'}
                                    hint={filtered
                                        ? 'Loosen the search or the dates, or clear them to see every transaction with this tag.'
                                        : 'Add the tag to a transaction in its register and it will be listed here.'}
                                    action={filtered
                                        ? <Button type="button" variant="secondary" size="sm" onClick={clearFilters}>Clear filters</Button>
                                        : undefined}
                                />
                            ) : (
                                <Panel>
                                    <div className="overflow-x-auto">
                                        <table className="w-full text-sm">
                                            <thead>
                                                <tr className="border-b border-border bg-surface-header text-left text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">
                                                    <th scope="col" className="px-3 py-2 font-semibold">Date</th>
                                                    <th scope="col" className="px-3 py-2 font-semibold">Account</th>
                                                    <th scope="col" className="px-3 py-2 font-semibold">Payee</th>
                                                    <th scope="col" className="px-3 py-2 font-semibold">Category</th>
                                                    <th scope="col" className="px-3 py-2 font-semibold">Other tags</th>
                                                    <th scope="col" className="px-3 py-2 text-right font-semibold">Amount</th>
                                                </tr>
                                            </thead>
                                            <tbody className="divide-y divide-border">
                                                {lines.map((line) => (
                                                    <LineRow
                                                        key={line.legId}
                                                        line={line}
                                                        ledgerId={ledgerId}
                                                        thisTag={tag}
                                                    />
                                                ))}
                                            </tbody>
                                        </table>
                                    </div>
                                    {linesQuery.hasNextPage ? (
                                        <div className="flex items-center justify-between gap-3 border-t border-border px-3 py-2.5 text-xs text-text-muted">
                                            <span>Showing {lines.length} so far</span>
                                            <Button
                                                type="button"
                                                variant="secondary"
                                                size="sm"
                                                onClick={() => void linesQuery.fetchNextPage()}
                                                pending={linesQuery.isFetchingNextPage}
                                                pendingLabel="Loading…"
                                            >
                                                Load more
                                            </Button>
                                        </div>
                                    ) : null}
                                </Panel>
                            )}
                        </>
                    )}
                </div>
            </MainPane>

            {dialog?.kind === 'edit' && tag && tags ? (
                <TagEditDialog
                    ledgerId={ledgerId}
                    tag={tag}
                    allTags={tags}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                    onRequestMerge={(target: TagDto) => setDialog({ kind: 'merge', presetTargetId: target.id })}
                />
            ) : null}
            {dialog?.kind === 'merge' && tag && tags ? (
                <MergeTagDialog
                    ledgerId={ledgerId}
                    tag={tag}
                    allTags={tags}
                    presetTargetId={dialog.presetTargetId}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                    // This tag is gone after a merge. The page stays to say what happened, which is
                    // what a move to the list would lose: that page cannot know a merge was made here.
                    // Once the lists refresh it says the tag no longer exists, and leads back.
                    onMerged={(summary) => setMergeNotice(mergeSummaryText(summary))}
                />
            ) : null}
        </MainArea>
    );
}

function LineRow({ line, ledgerId, thisTag }: { line: TagTransactionLine; ledgerId: string; thisTag: TagDto | undefined }) {
    const others = line.tags.filter((n) => n.toLowerCase() !== thisTag?.name.toLowerCase());
    return (
        <tr className="hover:bg-surface-hover">
            <td className="whitespace-nowrap px-3 py-2.5 text-text-muted">{formatLedgerDate(line.postedAt)}</td>
            <td className="px-3 py-2.5 text-text">{line.accountName}</td>
            <td className="px-3 py-2.5">
                <Link
                    to="/ledgers/$ledgerId/accounts/$accountId"
                    params={{ ledgerId, accountId: line.accountId }}
                    search={{ focus: line.headerId }}
                    className={rowLinkClass()}
                >
                    {line.payee ?? '—'}
                </Link>
            </td>
            <td className="px-3 py-2.5 text-text-muted">{line.counterpartyAccountName ?? ''}</td>
            <td className="px-3 py-2.5 text-xs text-text-subtle">{others.join(', ')}</td>
            <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums text-text">
                {/* In the line's own currency: the page lists the lines of every account, and
                    labelling them all in one currency misstates every line kept in another. */}
                {formatCurrency(line.amount, line.currencyCode)}
            </td>
        </tr>
    );
}
