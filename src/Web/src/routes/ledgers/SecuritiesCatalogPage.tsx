import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from '@tanstack/react-router';

import {
    fetchSecurities,
    fetchVisibleLedgers,
    refreshQuotes,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import type {
    QuoteRunOutcome,
    SecuritySummary,
} from '@/lib/types';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { EmptyStateInline } from '@/components/ui/EmptyState';
import { Panel, PanelBody } from '@/components/ui/Panel';
import {
    MainArea,
    MainPane,
    TopBar,
} from '@/components/ui/SidebarLayout';
import { AddSecurityDialog } from './components/AddSecurityDialog';
import { formatPrice, formatQuantity, formatCurrency } from '@/lib/money';

/**
 * `/ledgers/:lid/securities` — full Securities catalog. Searchable
 * table + `+ Add security` dialog. Row click navigates to the
 * Detail page. Deactivation lives on Detail (Q1 — modal-on-catalog,
 * inline-deactivate-on-detail keeps the catalog focused on adds).
 */
export function SecuritiesCatalogPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    const [search, setSearch] = useState('');
    const [addOpen, setAddOpen] = useState(false);
    const [refreshOutcome, setRefreshOutcome] =
        useState<QuoteRunOutcome | null>(null);

    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    const securitiesQuery = useQuery({
        queryKey: ['securities', ledgerId, search],
        queryFn: () => fetchSecurities(ledgerId, search),
    });

    // ADR-0054 D3: on-demand, ledger-wide price refresh. Fans out to every
    // enabled quote provider server-side; the outcome (new / updated /
    // unresolved) renders inline. Invalidates the catalog so refreshed
    // "Latest price" cells repaint.
    const refreshMutation = useMutation({
        mutationFn: () => refreshQuotes(ledgerId),
        onSuccess: (outcome) => {
            setRefreshOutcome(outcome);
            void queryClient.invalidateQueries({
                queryKey: ['securities', ledgerId],
            });
        },
    });

    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);
    const rows = securitiesQuery.data;

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: ledger?.name ?? 'Ledger',
                            node: ledger ? (
                                <Link
                                    to="/ledgers/$ledgerId"
                                    params={{ ledgerId }}
                                    className="hover:text-text"
                                >
                                    {ledger.name}
                                </Link>
                            ) : (
                                'Ledger'
                            ),
                        },
                        { label: 'Securities' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-6xl space-y-4 p-5">
                    <header className="flex items-start justify-between gap-4">
                        <div>
                            <h1 className="text-xl font-semibold tracking-tight">
                                Securities
                            </h1>
                            <p className="mt-0.5 text-sm text-text-muted">
                                Catalog of investment instruments referenced by
                                this ledger's transactions.
                            </p>
                        </div>
                        <div className="flex items-center gap-2">
                            <Button
                                variant="secondary"
                                size="sm"
                                onClick={() => refreshMutation.mutate()}
                                disabled={refreshMutation.isPending}
                            >
                                {refreshMutation.isPending
                                    ? 'Refreshing…'
                                    : 'Refresh prices'}
                            </Button>
                            <Button
                                variant="primary"
                                size="sm"
                                onClick={() => setAddOpen(true)}
                            >
                                + Add security
                            </Button>
                        </div>
                    </header>

                    {refreshMutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            Could not refresh prices:{' '}
                            {errorMessage(refreshMutation.error, 'Could not load securities.')}
                        </p>
                    ) : refreshOutcome ? (
                        <p className="text-sm text-text-muted">
                            {summarizeRefresh(refreshOutcome)}
                        </p>
                    ) : null}

                    <div className="flex items-center gap-3">
                        <input
                            type="search"
                            value={search}
                            onChange={(e) => setSearch(e.target.value)}
                            placeholder="Search by ticker, CUSIP, or name…"
                            className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                        <span className="whitespace-nowrap font-mono text-[0.6875rem] tabular-nums text-text-muted">
                            {rows?.length ?? 0} securities
                        </span>
                    </div>

                    {securitiesQuery.isError ? (
                        <Panel className="border-state-danger/40 bg-state-danger-soft">
                            <PanelBody>
                                <p role="alert" className="text-sm text-state-danger">
                                    {errorMessage(securitiesQuery.error, 'Could not load securities.')}
                                </p>
                            </PanelBody>
                        </Panel>
                    ) : null}

                    <Panel>
                        {securitiesQuery.isPending ? (
                            <PanelBody>
                                <p className="text-sm text-text-subtle">Loading…</p>
                            </PanelBody>
                        ) : !rows || rows.length === 0 ? (
                            <EmptyStateInline
                                message={
                                    search.length > 0
                                        ? `No securities match "${search}".`
                                        : 'No securities yet.'
                                }
                                hint={
                                    search.length > 0
                                        ? 'Try a different search.'
                                        : 'Add one manually or import from Moneydance.'
                                }
                            />
                        ) : (
                            <CatalogTable
                                ledgerId={ledgerId}
                                rows={rows}
                                onRowClick={(securityId) =>
                                    void navigate({
                                        to: '/ledgers/$ledgerId/securities/$securityId',
                                        params: { ledgerId, securityId },
                                    })
                                }
                            />
                        )}
                    </Panel>
                </div>
            </MainPane>

            {addOpen ? (
                <AddSecurityDialog
                    ledgerId={ledgerId}
                    onClose={() => setAddOpen(false)}
                    onCreated={(newId) => {
                        setAddOpen(false);
                        // Invalidate so the catalog refetches with
                        // the new row visible.
                        queryClient.invalidateQueries({
                            queryKey: ['securities', ledgerId],
                        });
                        void navigate({
                            to: '/ledgers/$ledgerId/securities/$securityId',
                            params: { ledgerId, securityId: newId },
                        });
                    }}
                />
            ) : null}
        </MainArea>
    );
}

function CatalogTable({
    ledgerId,
    rows,
    onRowClick,
}: {
    ledgerId: string;
    rows: readonly SecuritySummary[];
    onRowClick: (id: string) => void;
}) {
    void ledgerId;
    // Match the Ledger Hub's Securities-section order so a user
    // bouncing between the hub and the full catalog sees the same
    // sequence. Sort key: market value desc (qty × latest price),
    // falling back to raw qty when no price, alpha by name as the
    // final tiebreaker.
    const sorted = [...rows].sort((a, b) => {
        const av = sortKey(a);
        const bv = sortKey(b);
        if (bv !== av) return bv - av;
        return a.name.localeCompare(b.name);
    });
    return (
        <table className="w-full text-sm">
            <thead className="border-b border-border bg-surface-muted/40 text-[0.6875rem] uppercase tracking-wider text-text-muted">
                <tr>
                    <th className="px-4 py-2 text-left font-semibold">Ticker</th>
                    <th className="px-4 py-2 text-left font-semibold">CUSIP</th>
                    <th className="px-4 py-2 text-left font-semibold">Name</th>
                    <th className="px-4 py-2 text-left font-semibold">Asset class</th>
                    <th className="px-4 py-2 text-right font-semibold">Total qty</th>
                    <th className="px-4 py-2 text-right font-semibold">Latest price</th>
                    <th className="px-4 py-2 text-right font-semibold">Amount</th>
                </tr>
            </thead>
            <tbody className="divide-y divide-border/60">
                {sorted.map((s) => (
                    <tr
                        key={s.id}
                        onClick={() => onRowClick(s.id)}
                        className={
                            'cursor-pointer transition-colors hover:bg-surface-hover ' +
                            (s.isActive ? '' : 'text-text-subtle')
                        }
                    >
                        <td className="px-4 py-2 font-medium">
                            {s.ticker ?? <span className="text-text-subtle">—</span>}
                        </td>
                        <td className="px-4 py-2 font-mono text-[0.75rem] tabular-nums text-text-muted">
                            {s.cusip ?? <span className="text-text-subtle">—</span>}
                        </td>
                        <td className="px-4 py-2">{s.name}</td>
                        <td className="px-4 py-2 text-[0.6875rem] uppercase tracking-wider text-text-muted">
                            {s.assetClass?.replace(/_/g, ' ') ?? '—'}
                        </td>
                        <td className="px-4 py-2 text-right font-mono tabular-nums">
                            {s.totalQuantity !== 0
                                ? formatQuantity(s.totalQuantity)
                                : '—'}
                        </td>
                        <td className="px-4 py-2 text-right font-mono tabular-nums">
                            {s.latestPrice !== null
                                ? formatPrice(s.latestPrice)
                                : '—'}
                        </td>
                        <td className="px-4 py-2 text-right font-mono tabular-nums">
                            {s.totalQuantity !== 0 && s.latestPrice !== null
                                ? formatCurrency(s.totalQuantity * s.latestPrice)
                                : '—'}
                        </td>
                    </tr>
                ))}
            </tbody>
        </table>
    );
}

/** Sort key for the catalog list — matches the Ledger Hub's
 *  Securities-section order. Market value when priced
 *  (qty × latest); raw qty as fallback when no price exists. Zero
 *  for never-held securities so they sort to the end. */
function sortKey(s: SecuritySummary): number {
    if (s.totalQuantity === 0) return 0;
    if (s.latestPrice !== null) return Math.abs(s.totalQuantity * s.latestPrice);
    return Math.abs(s.totalQuantity);
}

/** One-line summary of a price-refresh outcome for the inline status. */
function summarizeRefresh(o: QuoteRunOutcome): string {
    const changed = o.pricesInserted + o.pricesUpdated;
    if (changed === 0) {
        return o.securitiesUnresolved.length > 0
            ? `No prices updated — ${o.securitiesUnresolved.length} ${
                  o.securitiesUnresolved.length === 1
                      ? 'security'
                      : 'securities'
              } had no available quote.`
            : 'Prices are already up to date.';
    }
    const parts = [`${o.pricesInserted} new`];
    if (o.pricesUpdated > 0) parts.push(`${o.pricesUpdated} updated`);
    if (o.securitiesUnresolved.length > 0) {
        parts.push(`${o.securitiesUnresolved.length} unresolved`);
    }
    return `Refreshed prices: ${parts.join(' · ')}.`;
}

