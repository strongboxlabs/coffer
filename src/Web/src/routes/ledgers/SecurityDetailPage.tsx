import { useEffect, useMemo, useState } from 'react';
import {
    useMutation,
    useQuery,
    useQueryClient,
    useInfiniteQuery,
} from '@tanstack/react-query';
import { Link, useNavigate, useParams } from '@tanstack/react-router';

import {
    ApiError,
    addSecurityPrice,
    deleteSecurityPrice,
    fetchSecurity,
    fetchSecurityPrices,
    fetchSecurityTransactions,
    fetchSecurityComponents,
    replaceSecurityComponents,
    fetchVisibleLedgers,
    patchSecurity,
    patchSecurityPrice,
} from '@/lib/api';
import type {
    CreateSecurityPriceRequest,
    PatchSecurityPriceRequest,
    PatchSecurityRequest,
    SecurityComponent,
    SecurityDetail,
    SecurityPriceRow,
    SecurityTransaction,
} from '@/lib/types';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { EmptyStateInline } from '@/components/ui/EmptyState';
import { Modal } from '@/components/ui/Modal';
import { SecurityFormFields, useSecurityForm } from './components/SecurityForm';
import { LedgerHubSection } from '@/components/LedgerHubSection';
import { Panel, PanelBody } from '@/components/ui/Panel';
import {
    MainArea,
    MainPane,
    TopBar,
} from '@/components/ui/SidebarLayout';
import { formatLedgerDate as formatDate } from '@/lib/dates';
import { errorMessage } from '@/lib/errorMessage';
import { formatPrice, formatQuantity } from '@/lib/money';

/**
 * `/ledgers/:lid/securities/:sid` — Securities Detail.
 *
 *   Hero: ticker · name, asset class, latest price + as-of, total qty,
 *   cost basis, market value, unrealized gain.
 *   Transactions: collapsible LedgerHubSection, cursor-paginated
 *     "Load more"; all rows reachable.
 *   Prices: collapsible LedgerHubSection, cursor-paginated; per-row
 *     edit + delete, headerAction adds a fresh price.
 *
 * Collapsible state persists per (securityId, section) — bouncing
 * between securities preserves each one's layout.
 */
export function SecurityDetailPage() {
    const { ledgerId, securityId } = useParams({ strict: false }) as {
        ledgerId: string;
        securityId: string;
    };

    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    const detailQuery = useQuery({
        queryKey: ['security', ledgerId, securityId],
        queryFn: () => fetchSecurity(ledgerId, securityId),
    });

    const txnsQuery = useInfiniteQuery({
        queryKey: ['security-transactions', ledgerId, securityId],
        queryFn: ({ pageParam }) =>
            fetchSecurityTransactions(ledgerId, securityId, {
                cursor: pageParam,
                limit: 50,
            }),
        initialPageParam: null as string | null,
        getNextPageParam: (last) => last.cursorForOlder,
        enabled: detailQuery.isSuccess,
    });

    const pricesQuery = useInfiniteQuery({
        queryKey: ['security-prices', ledgerId, securityId],
        queryFn: ({ pageParam }) =>
            fetchSecurityPrices(ledgerId, securityId, {
                cursor: pageParam,
                limit: 100,
            }),
        initialPageParam: null as string | null,
        getNextPageParam: (last) => last.cursorForOlder,
        enabled: detailQuery.isSuccess,
    });

    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);
    const detail = detailQuery.data;

    const [editOpen, setEditOpen] = useState(false);
    const [priceEditor, setPriceEditor] = useState<
        | { kind: 'add' }
        | { kind: 'edit'; row: SecurityPriceRow }
        | null
    >(null);

    const txnRows = useMemo(
        () => (txnsQuery.data?.pages.flatMap((p) => p.items) ?? []),
        [txnsQuery.data],
    );
    const priceRows = useMemo(
        () => (pricesQuery.data?.pages.flatMap((p) => p.items) ?? []),
        [pricesQuery.data],
    );

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
                        {
                            label: 'Securities',
                            node: (
                                <Link
                                    to="/ledgers/$ledgerId/securities"
                                    params={{ ledgerId }}
                                    className="hover:text-text"
                                >
                                    Securities
                                </Link>
                            ),
                        },
                        {
                            label: detail?.ticker
                                ? `${detail.ticker} · ${detail.name}`
                                : detail?.name ?? 'Security',
                        },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    {detailQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : detailQuery.isError ? (
                        <Panel className="border-state-danger/40 bg-state-danger-soft">
                            <PanelBody>
                                <p role="alert" className="text-sm text-state-danger">
                                    {errorMessage(detailQuery.error, 'Could not load this security.')}
                                </p>
                            </PanelBody>
                        </Panel>
                    ) : detail ? (
                        <>
                            <Hero
                                detail={detail}
                                onEdit={() => setEditOpen(true)}
                            />
                            <LedgerHubSection
                                sectionKey="transactions"
                                ledgerId={securityId}
                                title="Transactions"
                                count={
                                    txnsQuery.data ? txnRows.length : undefined
                                }
                                totalCount={
                                    txnsQuery.data?.pages[0]?.totalCount
                                }
                            >
                                <TransactionsTable
                                    ledgerId={ledgerId}
                                    items={txnRows}
                                    isPending={txnsQuery.isPending}
                                    isError={txnsQuery.isError}
                                    error={txnsQuery.error}
                                    hasNext={txnsQuery.hasNextPage}
                                    fetchingNext={txnsQuery.isFetchingNextPage}
                                    onLoadMore={() => void txnsQuery.fetchNextPage()}
                                />
                            </LedgerHubSection>
                            <LedgerHubSection
                                sectionKey="prices"
                                ledgerId={securityId}
                                title="Prices"
                                count={
                                    pricesQuery.data ? priceRows.length : undefined
                                }
                                totalCount={
                                    pricesQuery.data?.pages[0]?.totalCount
                                }
                                headerAction={
                                    <button
                                        type="button"
                                        onClick={() => setPriceEditor({ kind: 'add' })}
                                        className="font-medium text-accent hover:underline"
                                    >
                                        + Add price
                                    </button>
                                }
                            >
                                <PricesTable
                                    rows={priceRows}
                                    isPending={pricesQuery.isPending}
                                    isError={pricesQuery.isError}
                                    error={pricesQuery.error}
                                    hasNext={pricesQuery.hasNextPage}
                                    fetchingNext={pricesQuery.isFetchingNextPage}
                                    onLoadMore={() => void pricesQuery.fetchNextPage()}
                                    onEdit={(row) => setPriceEditor({ kind: 'edit', row })}
                                />
                            </LedgerHubSection>
                        </>
                    ) : null}
                </div>
            </MainPane>

            {editOpen && detail ? (
                <EditSecurityDialog
                    ledgerId={ledgerId}
                    securityId={securityId}
                    initial={detail}
                    onClose={() => setEditOpen(false)}
                />
            ) : null}

            {priceEditor !== null ? (
                <PriceDialog
                    ledgerId={ledgerId}
                    securityId={securityId}
                    mode={priceEditor}
                    onClose={() => setPriceEditor(null)}
                />
            ) : null}
        </MainArea>
    );
}

function Hero({
    detail,
    onEdit,
}: {
    detail: SecurityDetail;
    onEdit: () => void;
}) {
    const marketValue =
        detail.latestPrice !== null
            ? detail.latestPrice * detail.totalQuantity
            : null;
    const unrealized =
        marketValue !== null ? marketValue - detail.totalCostBasis : null;
    const costPerShare =
        detail.totalQuantity !== 0
            ? detail.totalCostBasis / detail.totalQuantity
            : null;

    return (
        <Panel>
            <PanelBody>
                <div className="flex items-start justify-between gap-4">
                    <div>
                        <h1 className="flex items-baseline gap-2 text-xl font-semibold tracking-tight">
                            {detail.ticker ? (
                                <span className="font-mono">{detail.ticker}</span>
                            ) : null}
                            <span>{detail.name}</span>
                        </h1>
                        <p className="mt-0.5 text-[0.6875rem] font-medium uppercase tracking-wider text-text-subtle">
                            {detail.assetClass?.replace(/_/g, ' ') ?? 'unclassified'}
                            {!detail.isActive ? ' · deactivated' : ''}
                        </p>
                    </div>
                    <Button variant="secondary" size="sm" onClick={onEdit}>
                        Edit
                    </Button>
                </div>
                <div className="mt-4 grid grid-cols-2 gap-4 md:grid-cols-3">
                    <Stat
                        label="Latest price"
                        value={
                            detail.latestPrice !== null
                                ? formatPrice(detail.latestPrice)
                                : '—'
                        }
                        sublabel={
                            detail.latestPriceAsOf
                                ? `as of ${formatDate(detail.latestPriceAsOf)}`
                                : undefined
                        }
                    />
                    <Stat
                        label="Total quantity"
                        value={
                            detail.totalQuantity !== 0
                                ? formatQuantity(detail.totalQuantity)
                                : '0'
                        }
                    />
                    <Stat
                        label="Cost basis"
                        value={formatPrice(detail.totalCostBasis)}
                        sublabel={
                            costPerShare !== null
                                ? `avg ${formatPrice(costPerShare)}/sh`
                                : undefined
                        }
                    />
                    <Stat
                        label="Market value"
                        value={marketValue !== null ? formatPrice(marketValue) : '—'}
                    />
                    <Stat
                        label="Unrealized"
                        value={unrealized !== null ? formatPrice(unrealized) : '—'}
                        tone={
                            unrealized === null
                                ? undefined
                                : unrealized >= 0
                                  ? 'positive'
                                  : 'negative'
                        }
                    />
                </div>
            </PanelBody>
        </Panel>
    );
}

function Stat({
    label,
    value,
    sublabel,
    tone,
}: {
    label: string;
    value: string;
    sublabel?: string;
    tone?: 'positive' | 'negative';
}) {
    return (
        <div>
            <p className="text-[0.6875rem] font-medium uppercase tracking-wider text-text-muted">
                {label}
            </p>
            <p
                className={
                    'mt-0.5 font-mono text-base tabular-nums ' +
                    (tone === 'positive'
                        ? 'text-state-success'
                        : tone === 'negative'
                          ? 'text-state-danger'
                          : '')
                }
            >
                {value}
            </p>
            {sublabel ? (
                <p className="text-[0.6875rem] text-text-subtle">{sublabel}</p>
            ) : null}
        </div>
    );
}

const LT_CLASSES = ['equity', 'fixed_income', 'cash', 'real_assets', 'alternative'];
const LT_REGIONS = ['us', 'developed_ex_us', 'emerging', 'global', 'na'];

/**
 * Controlled multi-asset look-through sleeve editor (ADR-0067): edit the
 * asset-class × optional-region weights the allocation tool decomposes a
 * multi-asset wrapper into. Pure UI — the owning Edit dialog holds the rows and
 * persists them (PUT replace) alongside the rest of the classification on Save.
 * Shown only when the security's asset class is `multi_asset`.
 */
function SleeveEditor({
    rows,
    setRows,
}: {
    rows: SecurityComponent[];
    setRows: (next: SecurityComponent[]) => void;
}) {
    const total = rows.reduce((s, r) => s + (Number.isFinite(r.weight) ? r.weight : 0), 0);
    const sel = 'rounded border border-border bg-surface px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';
    const update = (i: number, patch: Partial<SecurityComponent>) =>
        setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));

    return (
        <div className="flex flex-col gap-2 rounded border border-border/60 bg-surface-muted/20 p-2">
            <div className="flex items-center justify-between">
                <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">
                    Look-through sleeves
                </span>
                <span className={'font-mono text-xs tabular-nums ' + (Math.abs(total - 100) < 0.05 ? 'text-state-success' : 'text-state-warning')}>
                    {total.toFixed(1)}%
                </span>
            </div>
            <p className="text-xs text-text-subtle">
                Weights (should total 100%) used to decompose this multi-asset fund in allocation reports.
            </p>
            {rows.map((r, i) => (
                <div key={i} className="flex items-center gap-2">
                    <select className={sel} value={r.assetClass} onChange={(e) => update(i, { assetClass: e.target.value })}>
                        {LT_CLASSES.map((c) => <option key={c} value={c}>{c.replace(/_/g, ' ')}</option>)}
                    </select>
                    <select className={sel} value={r.region ?? ''} onChange={(e) => update(i, { region: e.target.value || null })}>
                        <option value="">(any region)</option>
                        {LT_REGIONS.map((c) => <option key={c} value={c}>{c.replace(/_/g, ' ')}</option>)}
                    </select>
                    <input type="number" min="0" step="0.1" className={sel + ' w-20 text-right'} value={r.weight}
                        onChange={(e) => update(i, { weight: Number(e.target.value) })} />
                    <button type="button" className="text-xs text-state-danger" onClick={() => setRows(rows.filter((_, j) => j !== i))}>
                        Remove
                    </button>
                </div>
            ))}
            {rows.length === 0 ? <p className="text-xs text-text-subtle">No sleeves yet.</p> : null}
            <div>
                <Button type="button" variant="secondary" size="sm"
                    onClick={() => setRows([...rows, { assetClass: 'equity', region: null, weight: 0 }])}>
                    Add sleeve
                </Button>
            </div>
        </div>
    );
}

function TransactionsTable({
    ledgerId,
    items,
    isPending,
    isError,
    error,
    hasNext,
    fetchingNext,
    onLoadMore,
}: {
    ledgerId: string;
    items: readonly SecurityTransaction[];
    isPending: boolean;
    isError: boolean;
    error: unknown;
    hasNext: boolean;
    fetchingNext: boolean;
    onLoadMore: () => void;
}) {
    if (isPending) {
        return (
            <PanelBody>
                <p className="text-sm text-text-subtle">Loading…</p>
            </PanelBody>
        );
    }
    if (isError) {
        return (
            <PanelBody>
                <p role="alert" className="text-sm text-state-danger">
                    {errorMessage(error, 'Could not load this security.')}
                </p>
            </PanelBody>
        );
    }
    if (items.length === 0) {
        return (
            <EmptyStateInline message="No transactions touch this security yet." />
        );
    }

    // Columns ordered: Date · Account · Action · Qty · Price · Amount.
    // Account follows Date so the user sees the brokerage first
    // (matches how they think about the transaction); action stays
    // adjacent since it qualifies the row's intent.
    return (
        <>
            <table className="w-full text-sm">
                <thead className="border-b border-border text-[0.6875rem] uppercase tracking-wider text-text-muted">
                    <tr>
                        <th className="px-4 py-2 text-left font-semibold">Date</th>
                        <th className="px-4 py-2 text-left font-semibold">Account</th>
                        <th className="px-4 py-2 text-left font-semibold">Action</th>
                        <th className="px-4 py-2 text-right font-semibold">Qty</th>
                        <th className="px-4 py-2 text-right font-semibold">Price</th>
                        <th className="px-4 py-2 text-right font-semibold">Amount</th>
                    </tr>
                </thead>
                <tbody className="divide-y divide-border/60">
                    {items.map((t) => (
                        <tr key={`${t.headerId}-${t.accountId}`} className="hover:bg-surface-hover">
                            <td className="px-4 py-2 font-mono text-[0.75rem] tabular-nums">
                                {formatDate(t.postedAt)}
                            </td>
                            <td className="px-4 py-2">
                                <Link
                                    to="/ledgers/$ledgerId/accounts/$accountId"
                                    params={{ ledgerId, accountId: t.accountId }}
                                    search={{ focus: t.headerId }}
                                    className="text-accent hover:underline"
                                >
                                    {t.accountName}
                                </Link>
                            </td>
                            <td className="px-4 py-2 text-[0.6875rem] uppercase tracking-wider text-text-muted">
                                {t.action ?? '—'}
                            </td>
                            <td
                                className={
                                    'px-4 py-2 text-right font-mono tabular-nums ' +
                                    (t.quantity !== null && t.quantity < 0
                                        ? 'text-state-danger'
                                        : '')
                                }
                            >
                                {t.quantity !== null ? formatQuantity(t.quantity) : '—'}
                            </td>
                            <td className="px-4 py-2 text-right font-mono tabular-nums">
                                {t.unitPrice !== null ? formatPrice(t.unitPrice) : '—'}
                            </td>
                            <td
                                className={
                                    'px-4 py-2 text-right font-mono tabular-nums ' +
                                    (t.amount < 0 ? 'text-state-danger' : '')
                                }
                            >
                                {formatPrice(t.amount)}
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
            {hasNext ? (
                <div className="border-t border-border/60 px-4 py-2 text-center">
                    <button
                        type="button"
                        onClick={onLoadMore}
                        disabled={fetchingNext}
                        className="text-xs font-medium text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-50"
                    >
                        {fetchingNext ? 'Loading…' : 'Load more'}
                    </button>
                </div>
            ) : null}
        </>
    );
}

/** Human label for a price's origin (security_prices.source, ADR-0070). */
const PRICE_SOURCE_LABELS: Record<string, string> = {
    simplefin: 'SimpleFIN',
    fetch: 'Market data',
    manual: 'Manual',
    import: 'Imported',
};
function priceSourceLabel(source: string): string {
    return PRICE_SOURCE_LABELS[source] ?? source;
}

function PricesTable({
    rows,
    isPending,
    isError,
    error,
    hasNext,
    fetchingNext,
    onLoadMore,
    onEdit,
}: {
    rows: readonly SecurityPriceRow[];
    isPending: boolean;
    isError: boolean;
    error: unknown;
    hasNext: boolean;
    fetchingNext: boolean;
    onLoadMore: () => void;
    onEdit: (row: SecurityPriceRow) => void;
}) {
    if (isPending) {
        return (
            <PanelBody>
                <p className="text-sm text-text-subtle">Loading…</p>
            </PanelBody>
        );
    }
    if (isError) {
        return (
            <PanelBody>
                <p role="alert" className="text-sm text-state-danger">
                    {errorMessage(error, 'Could not load this security.')}
                </p>
            </PanelBody>
        );
    }
    if (rows.length === 0) {
        return (
            <EmptyStateInline message="No prices recorded yet." />
        );
    }
    return (
        <>
            <table className="w-full text-sm">
                <thead className="border-b border-border text-[0.6875rem] uppercase tracking-wider text-text-muted">
                    <tr>
                        <th className="px-4 py-2 text-left font-semibold">As of</th>
                        <th className="px-4 py-2 text-right font-semibold">Price</th>
                        <th className="px-4 py-2 text-right font-semibold">High</th>
                        <th className="px-4 py-2 text-right font-semibold">Low</th>
                        <th className="px-4 py-2 text-right font-semibold">Volume</th>
                        <th className="px-4 py-2 text-left font-semibold">Source</th>
                        <th className="px-4 py-2 text-right font-semibold" aria-hidden></th>
                    </tr>
                </thead>
                <tbody className="divide-y divide-border/60">
                    {rows.map((p) => (
                        <tr
                            key={p.id}
                            onClick={() => onEdit(p)}
                            className="cursor-pointer hover:bg-surface-hover"
                        >
                            <td className="px-4 py-2 font-mono text-[0.75rem] tabular-nums">
                                {formatDate(p.asOf)}
                            </td>
                            <td className="px-4 py-2 text-right font-mono tabular-nums">
                                {formatPrice(p.price)}
                            </td>
                            <td className="px-4 py-2 text-right font-mono tabular-nums text-text-muted">
                                {p.high !== null ? formatPrice(p.high) : '—'}
                            </td>
                            <td className="px-4 py-2 text-right font-mono tabular-nums text-text-muted">
                                {p.low !== null ? formatPrice(p.low) : '—'}
                            </td>
                            <td className="px-4 py-2 text-right font-mono tabular-nums text-text-muted">
                                {p.volume !== null ? formatInteger(p.volume) : '—'}
                            </td>
                            <td className="px-4 py-2 text-left text-[0.6875rem] text-text-muted">
                                {priceSourceLabel(p.source)}
                            </td>
                            <td className="px-4 py-2 text-right text-[0.6875rem] text-text-subtle">
                                edit →
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
            {hasNext ? (
                <div className="border-t border-border/60 px-4 py-2 text-center">
                    <button
                        type="button"
                        onClick={onLoadMore}
                        disabled={fetchingNext}
                        className="text-xs font-medium text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-50"
                    >
                        {fetchingNext ? 'Loading…' : 'Load more'}
                    </button>
                </div>
            ) : null}
        </>
    );
}

function EditSecurityDialog({
    ledgerId,
    securityId,
    initial,
    onClose,
}: {
    ledgerId: string;
    securityId: string;
    initial: SecurityDetail;
    onClose: () => void;
}) {
    const queryClient = useQueryClient();
    const navigate = useNavigate();

    const form = useSecurityForm(initial);
    const [isActive, setIsActive] = useState(initial.isActive);
    // Rich classification (ADR-0067).
    const [vehicleType, setVehicleType] = useState(initial.vehicleType ?? '');
    const [region, setRegion] = useState(initial.region ?? '');
    const [equitySize, setEquitySize] = useState(initial.equitySize ?? '');
    const [equityStyle, setEquityStyle] = useState(initial.equityStyle ?? '');
    const [fiDuration, setFiDuration] = useState(initial.fiDuration ?? '');
    const [fiCredit, setFiCredit] = useState(initial.fiCredit ?? '');
    const [taxCharacter, setTaxCharacter] = useState(initial.taxCharacter ?? '');
    // Look-through sleeves (ADR-0067) — loaded + persisted only for multi_asset.
    const [sleeves, setSleeves] = useState<SecurityComponent[]>([]);
    const componentsQuery = useQuery({
        queryKey: ['security-components', ledgerId, securityId],
        queryFn: () => fetchSecurityComponents(ledgerId, securityId),
        enabled: initial.assetClass === 'multi_asset',
    });
    useEffect(() => {
        if (componentsQuery.data) setSleeves(componentsQuery.data.map((r) => ({ ...r })));
    }, [componentsQuery.data]);

    const patchMutation = useMutation({
        mutationFn: async (body: PatchSecurityRequest) => {
            await patchSecurity(ledgerId, securityId, body);
            // Persist sleeves when this is — or was — a multi-asset security:
            // replace with the edited set when multi_asset, clear when switched away.
            if (form.assetClass === 'multi_asset')
                await replaceSecurityComponents(ledgerId, securityId, sleeves);
            else if (initial.assetClass === 'multi_asset')
                await replaceSecurityComponents(ledgerId, securityId, []);
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['security', ledgerId, securityId] });
            queryClient.invalidateQueries({ queryKey: ['securities', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['security-components', ledgerId, securityId] });
            onClose();
        },
    });

    const errorCode = patchMutation.error instanceof ApiError
        ? patchMutation.error.code
        : undefined;

    function handleSubmit(e: React.FormEvent) {
        e.preventDefault();
        patchMutation.mutate({
            ...form.buildPatchShared(),
            isActive,
            // Classification (ADR-0067): '' clears each. Style axes are sent only
            // for their asset class so the other pair is cleared when switching.
            vehicleType,
            region,
            equitySize: form.assetClass === 'equity' ? equitySize : '',
            equityStyle: form.assetClass === 'equity' ? equityStyle : '',
            fiDuration: form.assetClass === 'fixed_income' ? fiDuration : '',
            fiCredit: form.assetClass === 'fixed_income' ? fiCredit : '',
            taxCharacter,
        });
    }

    const clsSelect = 'w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

    return (
        <Modal open onClose={onClose} titleId="edit-security-title" className="max-w-md">
            <form onSubmit={handleSubmit}>
                <header className="border-b border-border px-4 py-3">
                    <h2 id="edit-security-title" className="text-base font-semibold">
                        Edit security
                    </h2>
                </header>
                <div className="space-y-3 p-4">
                    <SecurityFormFields
                        form={form}
                        errorCode={errorCode}
                        extras={
                            <>
                                <label className="flex flex-col gap-1">
                                    <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Vehicle</span>
                                    <select value={vehicleType} onChange={(e) => setVehicleType(e.target.value)} className={clsSelect}>
                                        <option value="">— None —</option>
                                        {['mutual_fund', 'etf', 'stock', 'money_market', 'cit', 'separate_account', 'plan_529', 'option', 'cd', 'bond', 'other']
                                            .map((v) => <option key={v} value={v}>{v.replace(/_/g, ' ')}</option>)}
                                    </select>
                                </label>
                                <label className="flex flex-col gap-1">
                                    <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Region</span>
                                    <select value={region} onChange={(e) => setRegion(e.target.value)} className={clsSelect}>
                                        <option value="">— None —</option>
                                        {['us', 'developed_ex_us', 'emerging', 'global', 'na']
                                            .map((v) => <option key={v} value={v}>{v.replace(/_/g, ' ')}</option>)}
                                    </select>
                                </label>
                                {form.assetClass === 'equity' ? (
                                    <div className="flex gap-2">
                                        <label className="flex flex-1 flex-col gap-1">
                                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Size</span>
                                            <select value={equitySize} onChange={(e) => setEquitySize(e.target.value)} className={clsSelect}>
                                                <option value="">—</option>
                                                {['large', 'mid', 'small'].map((v) => <option key={v} value={v}>{v}</option>)}
                                            </select>
                                        </label>
                                        <label className="flex flex-1 flex-col gap-1">
                                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Style</span>
                                            <select value={equityStyle} onChange={(e) => setEquityStyle(e.target.value)} className={clsSelect}>
                                                <option value="">—</option>
                                                {['value', 'blend', 'growth'].map((v) => <option key={v} value={v}>{v}</option>)}
                                            </select>
                                        </label>
                                    </div>
                                ) : null}
                                {form.assetClass === 'fixed_income' ? (
                                    <div className="flex gap-2">
                                        <label className="flex flex-1 flex-col gap-1">
                                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Duration</span>
                                            <select value={fiDuration} onChange={(e) => setFiDuration(e.target.value)} className={clsSelect}>
                                                <option value="">—</option>
                                                {['short', 'intermediate', 'long'].map((v) => <option key={v} value={v}>{v}</option>)}
                                            </select>
                                        </label>
                                        <label className="flex flex-1 flex-col gap-1">
                                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Credit</span>
                                            <select value={fiCredit} onChange={(e) => setFiCredit(e.target.value)} className={clsSelect}>
                                                <option value="">—</option>
                                                {['government', 'investment_grade', 'high_yield'].map((v) => <option key={v} value={v}>{v.replace(/_/g, ' ')}</option>)}
                                            </select>
                                        </label>
                                    </div>
                                ) : null}
                                <label className="flex flex-col gap-1">
                                    <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Tax character</span>
                                    <select value={taxCharacter} onChange={(e) => setTaxCharacter(e.target.value)} className={clsSelect}>
                                        <option value="">— None —</option>
                                        <option value="taxable">taxable</option>
                                        <option value="tax_managed">tax-managed</option>
                                        <option value="tax_exempt">tax-exempt</option>
                                    </select>
                                </label>
                                {form.assetClass === 'multi_asset' ? (
                                    <SleeveEditor rows={sleeves} setRows={setSleeves} />
                                ) : null}
                                <label className="flex items-center gap-2 text-sm">
                                    <input
                                        type="checkbox"
                                        checked={isActive}
                                        onChange={(e) => setIsActive(e.target.checked)}
                                    />
                                    <span>Active</span>
                                </label>
                            </>
                        }
                    />
                </div>
                <footer className="flex items-center justify-between gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                    <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={() => {
                            onClose();
                            void navigate({
                                to: '/ledgers/$ledgerId/securities',
                                params: { ledgerId },
                            });
                        }}
                    >
                        ← Back to catalog
                    </Button>
                    <div className="flex gap-2">
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={onClose}
                            disabled={patchMutation.isPending}
                        >
                            Cancel
                        </Button>
                        <Button
                            type="submit"
                            variant="primary"
                            size="sm"
                            disabled={patchMutation.isPending || !form.isValid}
                        >
                            {patchMutation.isPending ? 'Saving…' : 'Save'}
                        </Button>
                    </div>
                </footer>
            </form>
        </Modal>
    );
}

/**
 * Add / edit / delete a single price point. Mode = add (no row)
 * or edit (existing row pre-fills the inputs). Save dispatches
 * to addSecurityPrice / patchSecurityPrice; delete is only
 * available in edit mode and confirms before firing.
 */
function PriceDialog({
    ledgerId,
    securityId,
    mode,
    onClose,
}: {
    ledgerId: string;
    securityId: string;
    mode: { kind: 'add' } | { kind: 'edit'; row: SecurityPriceRow };
    onClose: () => void;
}) {
    const queryClient = useQueryClient();
    const initial = mode.kind === 'edit' ? mode.row : null;

    const [priceDate, setPriceDate] = useState(() =>
        initial !== null
            ? initial.asOf.slice(0, 10)
            : new Date().toISOString().slice(0, 10),
    );
    const [priceStr, setPriceStr] = useState(() =>
        initial !== null ? String(initial.price) : '',
    );
    const [highStr, setHighStr] = useState(() =>
        initial?.high !== null && initial?.high !== undefined
            ? String(initial.high)
            : '',
    );
    const [lowStr, setLowStr] = useState(() =>
        initial?.low !== null && initial?.low !== undefined
            ? String(initial.low)
            : '',
    );
    const [volumeStr, setVolumeStr] = useState(() =>
        initial?.volume !== null && initial?.volume !== undefined
            ? String(initial.volume)
            : '',
    );

    const invalidate = () => {
        queryClient.invalidateQueries({
            queryKey: ['security', ledgerId, securityId],
        });
        queryClient.invalidateQueries({
            queryKey: ['security-prices', ledgerId, securityId],
        });
        queryClient.invalidateQueries({
            queryKey: ['securities', ledgerId],
        });
    };

    const addMutation = useMutation({
        mutationFn: (body: CreateSecurityPriceRequest) =>
            addSecurityPrice(ledgerId, securityId, body),
        onSuccess: () => {
            invalidate();
            onClose();
        },
    });
    const patchMutation = useMutation({
        mutationFn: (body: PatchSecurityPriceRequest) =>
            patchSecurityPrice(ledgerId, securityId, initial!.id, body),
        onSuccess: () => {
            invalidate();
            onClose();
        },
    });
    const deleteMutation = useMutation({
        mutationFn: () =>
            deleteSecurityPrice(ledgerId, securityId, initial!.id),
        onSuccess: () => {
            invalidate();
            onClose();
        },
    });

    const [confirmDelete, setConfirmDelete] = useState(false);

    const busy =
        addMutation.isPending || patchMutation.isPending || deleteMutation.isPending;
    const errorCode = (() => {
        const e =
            addMutation.error ??
            patchMutation.error ??
            deleteMutation.error;
        return e instanceof ApiError ? e.code : undefined;
    })();
    const errorMessage = errorMessageForCode(errorCode);

    const priceNum = Number(priceStr);
    const saveDisabled =
        busy ||
        priceStr.trim().length === 0 ||
        Number.isNaN(priceNum) ||
        priceNum <= 0 ||
        priceDate.length === 0;

    function handleSubmit(e: React.FormEvent) {
        e.preventDefault();
        if (saveDisabled) return;
        const high = highStr.trim().length === 0 ? null : Number(highStr);
        const low = lowStr.trim().length === 0 ? null : Number(lowStr);
        const volume = volumeStr.trim().length === 0 ? null : Number(volumeStr);

        // price_date is a calendar DATE (ADR-0070); send the bare YYYY-MM-DD the
        // date input already holds — the API binds it straight to a DateOnly.
        if (mode.kind === 'add') {
            addMutation.mutate({
                price: priceNum,
                priceDate,
                high: high === null || Number.isNaN(high) ? null : high,
                low: low === null || Number.isNaN(low) ? null : low,
                volume: volume === null || Number.isNaN(volume) ? null : volume,
            });
        } else {
            patchMutation.mutate({
                price: priceNum,
                priceDate,
                high: high === null || Number.isNaN(high) ? null : high,
                low: low === null || Number.isNaN(low) ? null : low,
                volume: volume === null || Number.isNaN(volume) ? null : volume,
            });
        }
    }

    function handleDelete() {
        if (mode.kind !== 'edit') return;
        // Confirm-before-fire: the price is a single historical data point
        // and the delete is reversible only via re-entry, so a deliberate
        // confirmation is the right gate.
        setConfirmDelete(true);
    }

    return (
        <>
            <Modal
                open
                onClose={onClose}
                titleId="price-dialog-title"
                className="max-w-md"
            >
                <form onSubmit={handleSubmit}>
                    <header className="border-b border-border px-4 py-3">
                        <h2
                            id="price-dialog-title"
                            className="text-base font-semibold"
                        >
                            {mode.kind === 'add' ? 'Add price' : 'Edit price'}
                        </h2>
                    </header>
                <div className="space-y-3 p-4">
                    <label className="flex flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Date *</span>
                        <input
                            type="date"
                            value={priceDate}
                            onChange={(e) => setPriceDate(e.target.value)}
                            required
                            className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </label>
                    <label className="flex flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Price *</span>
                        <input
                            type="number"
                            step="any"
                            value={priceStr}
                            onChange={(e) => setPriceStr(e.target.value)}
                            required
                            autoFocus={mode.kind === 'add'}
                            className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </label>
                    <div className="grid grid-cols-2 gap-3">
                        <label className="flex flex-col gap-1">
                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">High</span>
                            <input
                                type="number"
                                step="any"
                                value={highStr}
                                onChange={(e) => setHighStr(e.target.value)}
                                className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                            />
                        </label>
                        <label className="flex flex-col gap-1">
                            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Low</span>
                            <input
                                type="number"
                                step="any"
                                value={lowStr}
                                onChange={(e) => setLowStr(e.target.value)}
                                className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                            />
                        </label>
                    </div>
                    <label className="flex flex-col gap-1">
                        <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">Volume</span>
                        <input
                            type="number"
                            step="1"
                            value={volumeStr}
                            onChange={(e) => setVolumeStr(e.target.value)}
                            className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </label>
                    {errorMessage !== null ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {errorMessage}
                        </p>
                    ) : null}
                </div>
                <footer className="flex items-center justify-between gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                    {mode.kind === 'edit' ? (
                        <Button
                            type="button"
                            variant="danger"
                            size="sm"
                            onClick={handleDelete}
                            disabled={busy}
                        >
                            {deleteMutation.isPending ? 'Deleting…' : 'Delete price'}
                        </Button>
                    ) : (
                        <span />
                    )}
                    <div className="flex gap-2">
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={onClose}
                            disabled={busy}
                        >
                            Cancel
                        </Button>
                        <Button
                            type="submit"
                            variant="primary"
                            size="sm"
                            disabled={saveDisabled}
                        >
                            {busy ? 'Saving…' : 'Save'}
                        </Button>
                    </div>
                </footer>
                </form>
            </Modal>

            {mode.kind === 'edit' ? (
                <ConfirmDialog
                    open={confirmDelete}
                    variant="danger"
                    title="Delete price?"
                    body={`Delete the price recorded on ${formatDate(initial!.asOf)}?`}
                    confirmLabel="Delete"
                    isConfirming={deleteMutation.isPending}
                    onConfirm={() =>
                        deleteMutation.mutate(undefined, {
                            onSettled: () => setConfirmDelete(false),
                        })
                    }
                    onCancel={() => setConfirmDelete(false)}
                />
            ) : null}
        </>
    );
}

function formatInteger(n: number): string {
    return new Intl.NumberFormat(undefined, {
        maximumFractionDigits: 0,
    }).format(n);
}


function errorMessageForCode(code: string | undefined): string | null {
    switch (code) {
        case 'security-price-required':
            return 'Price must be a positive number.';
        case 'security-price-date-required':
            return 'Date is required.';
        case 'security-price-date-conflict':
            return 'A price for this date already exists. Edit that row instead, or pick a different date.';
        case 'security-price-high-low-invalid':
            return 'High must be greater than or equal to Low.';
        case 'security-price-not-in-security':
            return 'This price no longer exists. Close the dialog and refresh.';
        case undefined:
        case null:
            return null;
        default:
            return 'Could not save the price.';
    }
}
