import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';

import {
    fetchDashboardPrefs,
    fetchLedgerOverview,
    fetchLedgerOperations,
    fetchUpcomingReminders,
    fetchVisibleLedgers,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { accountTypeMeta } from '@/lib/accountTypes';
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
                <Icon className="h-3.5 w-3.5" aria-hidden />
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
                                <span className="w-12 shrink-0 font-mono text-[0.6875rem] tabular-nums text-text-subtle">
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
