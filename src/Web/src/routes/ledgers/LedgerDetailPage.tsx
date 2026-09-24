import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';

import {
    fetchAccounts,
    fetchBudgetProgress,
    fetchDashboardPrefs,
    fetchLedgerOverview,
    fetchLedgerOperations,
    fetchUpcomingReminders,
    fetchVisibleLedgers,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { accountTypeMeta } from '@/lib/accountTypes';
import { buildAccountPathMap } from '@/lib/accountPath';
import { ceilingFromRows, overspentRows } from './budget/cumulative';
import { formatCurrency, formatSignedAmount } from '@/lib/money';
import type {
    LedgerOverview,
    LedgerSummary,
    OverviewAccountGroup,
    LedgerOperationSummary,
    UpcomingOccurrence,
} from '@/lib/types';
import {
    familyClass,
    formatRelative,
    ledgerOperationLabel,
    statusClass,
    summarizeLedgerOperation,
} from '@/lib/ledgerOperationDisplay';
import { resolveDashboardLayout, type ResolvedWidget } from '@/lib/dashboardWidgets';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { KpiTile } from '@/components/ui/KpiTile';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

/**
 * Ledger Overview (ADR-0056 slice 1). The financial summary you land on when
 * you open a ledger — net worth, balances per account, investments, what's due,
 * and recent activity. Replaces the old Ledger Hub and absorbs its navigation:
 * the Accounts panel lists every account (each row → its register), and the
 * header keeps the Reminders / Bank feeds / Activity / Settings links.
 */
export function LedgerDetailPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };

    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    const overviewQuery = useQuery({
        queryKey: ['overview', ledgerId],
        queryFn: () => fetchLedgerOverview(ledgerId),
    });
    const upcomingQuery = useQuery({
        queryKey: ['overview-upcoming', ledgerId],
        queryFn: () => {
            const today = new Date();
            const to = new Date(today.getTime() + 30 * 24 * 60 * 60 * 1000);
            return fetchUpcomingReminders(ledgerId, isoDate(today), isoDate(to));
        },
    });
    const activityQuery = useQuery({
        queryKey: ['overview-activity', ledgerId],
        queryFn: () => fetchLedgerOperations(ledgerId, { days: 30, limit: 5 }),
    });
    const dashPrefsQuery = useQuery({
        queryKey: ['dashboard-prefs', ledgerId],
        queryFn: () => fetchDashboardPrefs(ledgerId),
    });

    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);
    const overview = overviewQuery.data;

    return (
        <MainArea>
            <TopBar>
                {/* No "All ledgers /" root (ADR-0090). The breadcrumb states
                    where you are; getting to ledger management is "Manage
                    ledgers…" in the ledger dropdown, which is a first-class
                    entry rather than a crumb doing navigation work. */}
                <Breadcrumb items={[{ label: ledger?.name ?? 'Ledger' }]} />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <LedgerHeader ledger={ledger} />

                    {overviewQuery.isError ? (
                        <Panel className="border-state-danger/40 bg-state-danger-soft">
                            <PanelBody>
                                <p role="alert" className="text-sm text-state-danger">
                                    {errorMessage(overviewQuery.error, 'Could not load this ledger.')}
                                </p>
                            </PanelBody>
                        </Panel>
                    ) : null}

                    {overviewQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : overview ? (
                        <OverviewBody
                            overview={overview}
                            ledgerId={ledgerId}
                            layout={resolveDashboardLayout(dashPrefsQuery.data)}
                            upcoming={upcomingQuery.data}
                            upcomingPending={upcomingQuery.isPending}
                            activity={activityQuery.data}
                            activityPending={activityQuery.isPending}
                        />
                    ) : null}
                </div>
            </MainPane>
        </MainArea>
    );
}

/**
 * Renders the widgets the user kept, in the order they chose (ADR-0056 slice 3).
 * The net-worth strip sits full-width on top when visible; the remaining
 * widgets flow in a responsive two-column grid in the saved order.
 */
function OverviewBody({
    overview,
    ledgerId,
    layout,
    upcoming,
    upcomingPending,
    activity,
    activityPending,
}: {
    overview: LedgerOverview;
    ledgerId: string;
    layout: ResolvedWidget[];
    upcoming: UpcomingOccurrence[] | undefined;
    upcomingPending: boolean;
    activity: LedgerOperationSummary[] | undefined;
    activityPending: boolean;
}) {
    const visibleKeys = layout.filter((w) => w.visible).map((w) => w.key);
    const showStrip = visibleKeys.includes('net-worth');
    const panelKeys = visibleKeys.filter((k) => k !== 'net-worth');

    function renderPanel(key: string) {
        switch (key) {
            case 'accounts':
                return <AccountsWidget key={key} overview={overview} ledgerId={ledgerId} />;
            case 'investments':
                return <InvestmentsWidget key={key} overview={overview} ledgerId={ledgerId} />;
            case 'upcoming':
                return (
                    <UpcomingWidget
                        key={key}
                        ledgerId={ledgerId}
                        rows={upcoming}
                        isPending={upcomingPending}
                    />
                );
            case 'spending':
                return <SpendingWidget key={key} ledgerId={ledgerId} />;
            case 'activity':
                return (
                    <RecentActivityWidget
                        key={key}
                        ledgerId={ledgerId}
                        rows={activity}
                        isPending={activityPending}
                    />
                );
            default:
                return null;
        }
    }

    return (
        <>
            {showStrip ? <NetWorthStrip overview={overview} /> : null}
            {overview.mixedCurrency ? (
                <p className="text-[0.6875rem] text-text-muted">
                    Accounts span multiple currencies — totals are summed without
                    conversion.
                </p>
            ) : null}
            <div className="grid items-start gap-4 lg:grid-cols-2">
                {panelKeys.map(renderPanel)}
            </div>
        </>
    );
}

function LedgerHeader({ ledger }: { ledger: LedgerSummary | undefined }) {
    // Per-ledger destinations live in the persistent sidebar now (one click
    // from anywhere) — the Overview header is just title + role.
    return (
        <header>
            <h1 className="text-xl font-semibold tracking-tight">
                {ledger?.name ?? 'Ledger'}
            </h1>
            {ledger ? (
                <p className="mt-0.5 text-[0.6875rem] font-medium uppercase tracking-wider text-text-subtle">
                    {ledger.role}
                </p>
            ) : null}
        </header>
    );
}

function NetWorthStrip({ overview }: { overview: LedgerOverview }) {
    const c = overview.currencyCode;
    return (
        <div className="grid grid-cols-2 gap-px overflow-hidden rounded border border-border bg-border sm:grid-cols-4">
            <KpiTile
                label="Net worth"
                value={formatCurrency(overview.netWorth, c)}
                captionTone={overview.netWorth < 0 ? 'danger' : 'muted'}
            />
            <KpiTile label="Assets" value={formatCurrency(overview.totalAssets, c)} />
            <KpiTile
                label="Liabilities"
                value={formatCurrency(overview.totalLiabilities, c)}
                captionTone={overview.totalLiabilities < 0 ? 'danger' : 'muted'}
            />
            <KpiTile label="Investments" value={formatCurrency(overview.investmentsValue, c)} />
        </div>
    );
}

function AccountsWidget({
    overview,
    ledgerId,
}: {
    overview: LedgerOverview;
    ledgerId: string;
}) {
    return (
        <Panel>
            <PanelHead className="flex items-center justify-between">
                <span className="font-medium">Accounts</span>
                <Link
                    to="/ledgers/$ledgerId/accounts"
                    params={{ ledgerId }}
                    className="text-xs font-medium text-accent hover:underline"
                >
                    Manage accounts →
                </Link>
            </PanelHead>
            {overview.accountGroups.length === 0 ? (
                <PanelBody className="py-8 text-center">
                    <p className="text-sm font-medium">No accounts yet.</p>
                    <p className="mt-1 text-sm text-text-muted">
                        Import or add accounts to see balances here.
                    </p>
                </PanelBody>
            ) : (
                <div className="divide-y divide-border">
                    {overview.accountGroups.map((group) => (
                        <AccountGroup
                            key={group.accountType}
                            group={group}
                            ledgerId={ledgerId}
                        />
                    ))}
                </div>
            )}
        </Panel>
    );
}

function AccountGroup({
    group,
    ledgerId,
}: {
    group: OverviewAccountGroup;
    ledgerId: string;
}) {
    const meta = accountTypeMeta(group.accountType);
    const Icon = meta.icon;
    return (
        <div>
            <div className="flex items-center gap-2 bg-surface-muted/30 px-4 py-1.5 text-[0.6875rem] font-semibold uppercase tracking-wider text-text-muted">
                <Icon className="size-icon-sm" aria-hidden />
                <span>{meta.label}</span>
                <span className="ml-auto font-mono tabular-nums">
                    {formatCurrency(group.subtotal)}
                </span>
            </div>
            <ul aria-label={meta.label} className="divide-y divide-border/60">
                {group.accounts.map((account) => (
                    <li key={account.id}>
                        <Link
                            to="/ledgers/$ledgerId/accounts/$accountId"
                            params={{ ledgerId, accountId: account.id }}
                            className="flex items-center justify-between gap-3 px-4 py-2 transition-colors hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                        >
                            <span className="min-w-0 flex-1 truncate text-sm font-medium">
                                {account.name}
                            </span>
                            <span className="font-mono text-[0.6875rem] tabular-nums text-text-muted">
                                {formatCurrency(account.balance, account.currencyCode)}
                            </span>
                        </Link>
                    </li>
                ))}
            </ul>
        </div>
    );
}

/**
 * This month's spend against the budget — the dashboard's one-glance half of
 * budgeting.
 *
 * Still no empty state that demands configuration first: with no targets set
 * it judges against what is TYPICAL, which exists the moment there is
 * categorised spending because it is derived from history rather than
 * declared. Set a target and the same widget judges against that instead,
 * because `mark` is already whichever of the two applies (ADR-0099 D1).
 *
 * It used to say "no targets, nothing to set up" and mean it — the comparison
 * was hard-wired to `typical`. That outlived the targets shipping in 0.88.0,
 * and left the widget SELECTING rows one way and DISPLAYING them another:
 * `overspentRows` has always ranked by mark, so a category with typical 500,
 * target 200 and actual 300 was picked as over — then had its overage printed
 * against typical, rendering "+-$200.00". Both halves judge by mark now, and
 * the totals use the same `ceilingFromRows` the budget screen's hero draws, so
 * the widget and the screen behind "See all" cannot disagree.
 *
 * Fetches its own data rather than riding the overview aggregate, because
 * `OverviewRepository` is scoped to net worth and excludes categories by
 * construction — a deliberate scope, not an oversight.
 */
function SpendingWidget({ ledgerId }: { ledgerId: string }) {
    const query = useQuery({
        queryKey: ['budget-progress', ledgerId, 'current'],
        queryFn: () => fetchBudgetProgress(ledgerId),
        staleTime: 60_000,
    });
    const data = query.data;
    const currency = data?.currencyCode ?? 'USD';

    // Full paths, because this is a FLAT list with no tree around it. A real
    // ledger has two "Food" and two "Loan" under different parents, and two
    // identical rows carrying different numbers is worse than a longer label.
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
        staleTime: 60_000,
    });
    const paths = useMemo(
        () => buildAccountPathMap(accountsQuery.data ?? []),
        [accountsQuery.data],
    );

    // Categories running meaningfully hot, worst first. Shared with the budget
    // screen rather than reimplemented: this had its own copy of the filter and
    // sort, and had therefore MISSED the roots-only fix — the API rolls up, so a
    // parent row already contains its children and the list could report
    // "Taxes" and "Federal Income Tax" as two separate problems. The floor keeps
    // it quiet too: a category a pound over its average is not news, and a
    // widget that cries wolf every month stops being read.
    const hot = overspentRows(data?.rows ?? []).slice(0, 3);

    // The SAME ceiling the budget screen's hero draws — sum of the marks over
    // root rows — rather than `typicalTotal`, which ignores every target that
    // has been set. Two surfaces showing one month must not disagree about
    // what the month is measured against.
    const ceiling = ceilingFromRows(data?.rows ?? []);
    const anyTarget = (data?.rows ?? []).some((r) => r.target !== null);
    const barMax = Math.max(data?.actualTotal ?? 0, ceiling ?? 0) * 1.05 || 1;
    const barPct = (v: number) =>
        `${Math.max(0, Math.min(100, (v / barMax) * 100))}%`;

    return (
        <Panel>
            <PanelHead className="flex items-center justify-between">
                <span className="font-medium">Spending</span>
                <Link
                    to="/ledgers/$ledgerId/budget"
                    params={{ ledgerId }}
                    className="text-xs font-medium text-accent hover:underline"
                >
                    See all →
                </Link>
            </PanelHead>
            <PanelBody>
                {query.isPending ? (
                    <p className="text-sm text-text-muted">Loading…</p>
                ) : !data || data.rows.length === 0 ? (
                    <p className="text-sm text-text-muted">No categorised spending yet.</p>
                ) : (
                    <div className="space-y-2">
                        <div className="flex items-baseline justify-between gap-3">
                            <div>
                                <div className="font-mono text-lg font-bold tabular-nums">
                                    {formatCurrency(data.actualTotal, currency)}
                                </div>
                                <div className="text-[0.6875rem] text-text-muted">
                                    this month
                                </div>
                            </div>
                            <div className="text-right">
                                <div className="font-mono text-sm tabular-nums text-text-muted">
                                    {ceiling === null
                                        ? '—'
                                        : formatCurrency(ceiling, currency)}
                                </div>
                                <div className="text-[0.6875rem] text-text-muted">
                                    {anyTarget
                                        ? 'budgeted'
                                        : `usually, over ${data.windowMonths} months`}
                                </div>
                            </div>
                        </div>
                        {/* Same construction as the budget screen's bars: the
                            run to the mark in accent, the excess beyond it in
                            danger, the mark itself a tick. Drawn only when
                            there IS a ceiling — a bar against nothing would
                            imply a limit the ledger has not got. */}
                        {ceiling !== null ? (
                            <span aria-hidden className="relative block h-control-20px">
                                <span
                                    className="absolute top-1.5 h-fixed-8px rounded-l-full bg-accent"
                                    style={{ left: 0, width: barPct(Math.min(data.actualTotal, ceiling)) }}
                                />
                                {data.actualTotal > ceiling ? (
                                    <span
                                        className="absolute top-1.5 h-fixed-8px rounded-r-full bg-state-danger"
                                        style={{ left: barPct(ceiling), width: barPct(data.actualTotal - ceiling) }}
                                    />
                                ) : null}
                                <span
                                    className="absolute top-0 h-full w-fixed-2px bg-border-strong"
                                    style={{ left: barPct(ceiling) }}
                                />
                            </span>
                        ) : null}
                        {hot.length > 0 ? (
                            <ul className="space-y-0.5 border-t border-border/40 pt-2">
                                {hot.map((r) => (
                                    <li key={r.categoryId} className="flex justify-between gap-2 text-xs">
                                        <span
                                            className="truncate text-text-muted"
                                            title={paths.get(r.categoryId) ?? r.name}
                                        >
                                            {paths.get(r.categoryId) ?? r.name}
                                        </span>
                                        {/* Against the MARK, matching how
                                            overspentRows picked this row. */}
                                        <span className="shrink-0 font-mono tabular-nums text-state-danger">
                                            +{formatCurrency(r.actual - (r.mark ?? 0), currency)}
                                        </span>
                                    </li>
                                ))}
                            </ul>
                        ) : null}
                    </div>
                )}
            </PanelBody>
        </Panel>
    );
}

function InvestmentsWidget({
    overview,
    ledgerId,
}: {
    overview: LedgerOverview;
    ledgerId: string;
}) {
    const p = overview.portfolio;
    const hasHoldings = p.value !== 0 || p.costBasis !== 0;
    const gainTone = p.unrealizedGain < 0 ? 'text-state-danger' : 'text-state-success';
    return (
        <Panel>
            <PanelHead className="flex items-center justify-between">
                <span className="font-medium">Investments</span>
                <Link
                    to="/ledgers/$ledgerId/securities"
                    params={{ ledgerId }}
                    className="text-xs font-medium text-accent hover:underline"
                >
                    Manage securities →
                </Link>
            </PanelHead>
            <PanelBody>
                {!hasHoldings ? (
                    <p className="text-sm text-text-muted">No holdings yet.</p>
                ) : (
                    <div className="flex items-baseline justify-between gap-3">
                        <div>
                            <div className="font-mono text-lg font-bold tabular-nums">
                                {formatCurrency(p.value)}
                            </div>
                            <div className="text-[0.6875rem] text-text-muted">
                                portfolio value
                            </div>
                        </div>
                        <div className={`text-right font-mono text-sm tabular-nums ${gainTone}`}>
                            <div>{formatSignedAmount(p.unrealizedGain)}</div>
                            <div className="text-[0.6875rem]">
                                {p.percentChange >= 0 ? '+' : ''}
                                {p.percentChange.toFixed(2)}%
                            </div>
                        </div>
                    </div>
                )}
            </PanelBody>
        </Panel>
    );
}

function UpcomingWidget({
    ledgerId,
    rows,
    isPending,
}: {
    ledgerId: string;
    rows: UpcomingOccurrence[] | undefined;
    isPending: boolean;
}) {
    // Actionable/posted slots only (skip the read-only skipped trail), soonest
    // first, top 5.
    const items = (rows ?? [])
        .filter((r) => r.kind !== 'skipped')
        .slice()
        .sort((a, b) => a.date.localeCompare(b.date))
        .slice(0, 5);
    return (
        <Panel>
            <PanelHead className="flex items-center justify-between">
                <span className="font-medium">Upcoming</span>
                <Link
                    to="/ledgers/$ledgerId/reminders"
                    params={{ ledgerId }}
                    className="text-xs font-medium text-accent hover:underline"
                >
                    View all →
                </Link>
            </PanelHead>
            {isPending ? (
                <PanelBody>
                    <p className="text-sm text-text-subtle">Loading…</p>
                </PanelBody>
            ) : items.length === 0 ? (
                <PanelBody>
                    <p className="text-sm text-text-muted">Nothing due in the next 30 days.</p>
                </PanelBody>
            ) : (
                <ul className="divide-y divide-border/60">
                    {items.map((r, i) => (
                        <li
                            key={`${r.reminderId}-${r.date}-${i}`}
                            className="flex items-center justify-between gap-3 px-4 py-2 text-sm"
                        >
                            <span className="flex min-w-0 items-baseline gap-2">
                                <span className="w-fixed-48px shrink-0 font-mono text-[0.6875rem] tabular-nums text-text-subtle">
                                    {shortDate(r.date)}
                                </span>
                                <span className="truncate">{r.payee ?? 'Reminder'}</span>
                            </span>
                            <span className="font-mono text-[0.6875rem] tabular-nums text-text-muted">
                                {formatSignedAmount(r.amount)}
                            </span>
                        </li>
                    ))}
                </ul>
            )}
        </Panel>
    );
}

function RecentActivityWidget({
    ledgerId,
    rows,
    isPending,
}: {
    ledgerId: string;
    rows: LedgerOperationSummary[] | undefined;
    isPending: boolean;
}) {
    const items = rows ?? [];
    return (
        <Panel>
            <PanelHead className="flex items-center justify-between">
                <span className="font-medium">Recent activity</span>
                <Link
                    to="/ledgers/$ledgerId/settings"
                    params={{ ledgerId }}
                    search={{ tab: 'activity' }}
                    className="text-xs font-medium text-accent hover:underline"
                >
                    View activity →
                </Link>
            </PanelHead>
            {isPending ? (
                <PanelBody>
                    <p className="text-sm text-text-subtle">Loading…</p>
                </PanelBody>
            ) : items.length === 0 ? (
                <PanelBody>
                    <p className="text-sm text-text-muted">No recent syncs or refreshes.</p>
                </PanelBody>
            ) : (
                <ul className="divide-y divide-border/60">
                    {items.map((run) => (
                        <li
                            key={run.id}
                            className="flex items-center justify-between gap-3 px-4 py-2 text-sm"
                        >
                            <span className="flex min-w-0 items-center gap-2">
                                <span
                                    className={
                                        'rounded px-1.5 py-0.5 text-[0.625rem] font-semibold uppercase tracking-wider ' +
                                        familyClass(run.family)
                                    }
                                >
                                    {run.family}
                                </span>
                                <span className="truncate">{ledgerOperationLabel(run.providerKey)}</span>
                            </span>
                            <span className="flex shrink-0 items-center gap-2 text-[0.6875rem] text-text-muted">
                                <span className="font-mono tabular-nums">
                                    {summarizeLedgerOperation(run)}
                                </span>
                                <span
                                    className={
                                        'rounded-full px-1.5 py-0.5 text-[0.5625rem] font-semibold uppercase tracking-wider ' +
                                        statusClass(run.status)
                                    }
                                >
                                    {run.status}
                                </span>
                                <span className="text-text-subtle">{formatRelative(run.startedAt)}</span>
                            </span>
                        </li>
                    ))}
                </ul>
            )}
        </Panel>
    );
}


/** Date as local-ish 'YYYY-MM-DD' for the reminders window query. */
function isoDate(d: Date): string {
    return d.toISOString().slice(0, 10);
}

/** 'YYYY-MM-DD' → 'M/D' for the compact upcoming list. */
function shortDate(iso: string): string {
    const [, m, d] = iso.split('-');
    return `${Number(m)}/${Number(d)}`;
}
