import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';

import {
    deleteBudgetTarget,
    fetchAccounts,
    fetchBudgetProgress,
    fetchVisibleLedgers,
    fillBudgetTargets,
    setBudgetTarget,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { formatCurrency } from '@/lib/money';
import { addMonths, monthLabel, todayParts } from '@/lib/calendar';
import { buildAccountPathMap } from '@/lib/accountPath';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { EmptyState } from '@/components/ui/EmptyState';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';

import { BudgetHero } from './budget/BudgetHero';
import { CategoryRows } from './budget/CategoryRows';
import { ceilingFromRows, cumulative, overspentRows, rootRows } from './budget/cumulative';

/**
 * `/ledgers/:ledgerId/budget` — spend this month against what is normal.
 *
 * The design is ADR-0099. In short: there is nothing to configure, because the
 * mark every category is judged against is DERIVED from the trailing months
 * rather than declared. Most people never set a budget, and of those who do,
 * most want to notice a month that is out of the ordinary rather than manage
 * spending against a plan — so the screen has to be useful before anyone has
 * typed a number, and this one is.
 *
 * It says "normal", never "budget", because the figure describes history and
 * therefore MOVES when history moves — a backdated receipt, a late feed import,
 * a category merge that rewrites months long past. Stored targets arrive in a
 * later slice and will be frozen precisely because a person decided them; when
 * they do, the Normal column becomes an input and nothing else about this
 * screen changes shape.
 *
 * Expanding a row opens that category's own curve BENEATH it, rather than
 * drawing a sparkline in every row or swapping the hero out. A 150×22 chart
 * has no presence, and shrinking a chart is not the same as designing a small
 * one; taking the hero away, meanwhile, removes the overall picture at the
 * exact moment you start investigating a part of it. One row at a time, opened
 * on purpose, is the only version that keeps both.
 */
export function BudgetPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const today = todayParts();

    // The month is a URL search param, so the page is linkable and survives a
    // round trip to a register and back. Absent means this month.
    const search = useSearch({ strict: false }) as { month?: string };
    const navigate = useNavigate();
    const { year, month } = (() => {
        const m = search.month;
        if (m === undefined) return { year: today.year, month: today.month };
        return { year: Number(m.slice(0, 4)), month: Number(m.slice(5, 7)) };
    })();

    const setMonth = (next: { year: number; month: number }) => {
        const isNow = next.year === today.year && next.month === today.month;
        void navigate({
            to: '/ledgers/$ledgerId/budget',
            params: { ledgerId },
            // The current month drops the param rather than spelling itself
            // out, so the bare path stays the canonical URL for "now".
            search: isNow
                ? {}
                : { month: `${next.year.toString().padStart(4, '0')}-${next.month.toString().padStart(2, '0')}` },
            replace: true,
        });
    };
    // A SET, not one id: rows nest now, so auto-collapse would mean opening a
    // child closes its own parent.
    const [expandedIds, setExpandedIds] = useState<ReadonlySet<string>>(new Set());
    const toggleExpanded = (categoryId: string) => setExpandedIds((prev) => {
        const next = new Set(prev);
        if (!next.delete(categoryId)) next.add(categoryId);
        return next;
    });

    // Which row's TRANSACTIONS are open — one at a time, and separate from the
    // tree's expansion state. The panel is the expensive thing on this screen
    // (a row is ~29px, a panel several times that), so letting several stack up
    // is what buried the table in the first place.
    const [detailId, setDetailId] = useState<string | null>(null);
    const toggleDetail = (categoryId: string) =>
        setDetailId((prev) => (prev === categoryId ? null : categoryId));

    const monthKey = `${year.toString().padStart(4, '0')}-${month.toString().padStart(2, '0')}`;

    const ledgersQuery = useQuery({ queryKey: ['ledgers'], queryFn: fetchVisibleLedgers });
    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);

    const progressQuery = useQuery({
        queryKey: ['budget-progress', ledgerId, monthKey],
        queryFn: () => fetchBudgetProgress(ledgerId, monthKey),
    });
    const data = progressQuery.data;
    const currency = data?.currencyCode ?? 'USD';

    // Category names alone are ambiguous — a real ledger carries two
    // "Automobile" and two "Dental" under different parents, and a bare leaf
    // name makes them indistinguishable rows. The app already builds full
    // paths for exactly this; not using them was the omission.
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
        staleTime: 60_000,
    });
    const accountPaths = useMemo(
        () => buildAccountPathMap(accountsQuery.data ?? []),
        [accountsQuery.data],
    );

    // The hero is ALWAYS the whole month. An earlier cut swapped it for the
    // selected category, which took the overall picture away at the exact
    // moment you started investigating a part of it. A category's own curve
    // opens inside its row instead.
    const chart = useMemo(() => {
        if (!data) return null;
        return {
            series: cumulative(data.dailyActual, data.daysInMonth),
            normal: cumulative(data.dailyNormal, data.daysInMonth),
            // The SUM of the table's marks, so the hero and the table can
            // never disagree about the same month.
            ceiling: ceilingFromRows(data.rows),
        };
    }, [data]);

    const hot = useMemo(() => (data ? overspentRows(data.rows) : []), [data]);

    // ---- writing targets -------------------------------------------------
    //
    // Every write invalidates the whole month rather than patching the row in
    // place. It reads as heavier than it is (one request, already cached) and
    // it is the only honest option: a typed target changes its ANCESTORS' marks
    // too, per ADR-0099 D1a, and that composition is a tree walk the server
    // does. Patching one row locally would leave the parent's tick and the
    // hero's ceiling stale until something else refetched.
    const queryClient = useQueryClient();
    const [notice, setNotice] = useState<string | null>(null);
    const [pendingFill, setPendingFill] = useState<null | 'copy' | 'average'>(null);

    const refreshMonth = () => {
        void queryClient.invalidateQueries({
            queryKey: ['budget-progress', ledgerId, monthKey],
        });
        // The Overview's spending widget reads the CURRENT month under its own
        // key, so a target set here has to reach it too.
        void queryClient.invalidateQueries({
            queryKey: ['budget-progress', ledgerId, 'current'],
        });
    };

    const targetMut = useMutation({
        mutationFn: ({ categoryId, amount }: { categoryId: string; amount: number | null }) =>
            amount === null
                ? deleteBudgetTarget(ledgerId, categoryId, monthKey)
                : setBudgetTarget(ledgerId, { categoryId, month: monthKey, amount }),
        onSuccess: () => { setNotice(null); refreshMonth(); },
        // The cell disables the conflicts it can SEE, but the server walks the
        // whole tree and can refuse one the client could not: a conflicting
        // ancestor with no spending of its own is absent from these rows.
        onError: (e) => setNotice(errorMessage(e, 'Could not save that target.')),
    });

    const fillMut = useMutation({
        mutationFn: (entries: { categoryId: string; amount: number }[]) =>
            fillBudgetTargets(ledgerId, { month: monthKey, entries }),
        onSuccess: (res) => {
            refreshMonth();
            // Say what was skipped. Swallowing it is the failure this response
            // shape exists to prevent — one click standing in for thirty typed
            // decisions is the worst place for a silent partial success.
            setNotice(res.skipped.length === 0
                ? `Set ${res.applied} target${res.applied === 1 ? '' : 's'}.`
                : `Set ${res.applied}, skipped ${res.skipped.length}: `
                  + res.skipped
                      .map((k) => k.conflictingCategoryName ?? k.status)
                      .slice(0, 3)
                      .join(', ')
                  + (res.skipped.length > 3 ? ' and more' : '')
                  + ' — a target covers everything beneath it.');
        },
        onError: (e) => setNotice(errorMessage(e, 'Could not fill the targets.')),
    });

    // Both fills propose ROOTS only, matching what the table renders. Proposing
    // every row would ask the server to write a parent and its child in one
    // batch, which D1a forbids — it would skip half of them and report a
    // conflict the user never made.
    const runFill = async (kind: 'copy' | 'average') => {
        if (!data) return;
        if (kind === 'average') {
            fillMut.mutate(rootRows(data.rows)
                .filter((r) => r.typical !== null)
                .map((r) => ({ categoryId: r.categoryId, amount: Math.round(r.typical! * 100) / 100 })));
            return;
        }
        const prev = addMonths(year, month, -1);
        const prevKey = `${prev.year.toString().padStart(4, '0')}-${prev.month.toString().padStart(2, '0')}`;
        try {
            const last = await queryClient.fetchQuery({
                queryKey: ['budget-progress', ledgerId, prevKey],
                queryFn: () => fetchBudgetProgress(ledgerId, prevKey),
            });
            const entries = last.rows
                .filter((r) => r.target !== null)
                .map((r) => ({ categoryId: r.categoryId, amount: r.target! }));
            if (entries.length === 0) {
                setNotice(`${monthLabel(prev.year, prev.month)} has no targets to copy.`);
                return;
            }
            fillMut.mutate(entries);
        } catch (e) {
            setNotice(errorMessage(e, 'Could not read last month.'));
        }
    };

    const monthHasTargets = (data?.rows ?? []).some((r) => r.target !== null);

    const step = (delta: number) => {
        setMonth(addMonths(year, month, delta));
        // A category expanded in one month may not appear in the next.
        setExpandedIds(new Set());
        setDetailId(null);
    };
    const isCurrentMonth = year === today.year && month === today.month;

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
                            ) : (
                                'Ledger'
                            ),
                        },
                        { label: 'Budget' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header className="flex flex-wrap items-start justify-between gap-3">
                        <div>
                            <h1 className="text-xl font-semibold tracking-tight">Budget</h1>
                            <p className="mt-0.5 text-sm text-text-muted">
                                What you spent this month, beside what you usually spend.
                            </p>
                        </div>
                        <div className="flex items-center gap-1">
                            <button
                                type="button"
                                onClick={() => step(-1)}
                                aria-label="Previous month"
                                className="h-control-28px rounded border border-border px-2 text-xs hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                            >
                                ‹
                            </button>
                            <span className="min-w-fixed-128px text-center text-sm font-medium tabular-nums">
                                {monthLabel(year, month)}
                            </span>
                            <button
                                type="button"
                                onClick={() => step(1)}
                                aria-label="Next month"
                                className="h-control-28px rounded border border-border px-2 text-xs hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                            >
                                ›
                            </button>
                            <button
                                type="button"
                                onClick={() => {
                                    setMonth({ year: today.year, month: today.month });
                                    setExpandedIds(new Set());
        setDetailId(null);
                                }}
                                disabled={isCurrentMonth}
                                className="ml-1 h-control-28px rounded border border-border px-2 text-xs hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-40"
                            >
                                This month
                            </button>
                        </div>
                    </header>

                    {progressQuery.isPending ? (
                        <Panel><PanelBody><p className="text-sm text-text-muted">Loading…</p></PanelBody></Panel>
                    ) : progressQuery.isError ? (
                        <Panel><PanelBody>
                            <p role="alert" className="text-sm text-state-danger">
                                Could not load spending for {monthLabel(year, month)}.
                            </p>
                        </PanelBody></Panel>
                    ) : data && data.actualTotal === 0 && data.latestMonthWithSpending ? (
                        // Nothing this month, but there IS data. Say where it is
                        // and offer the jump rather than silently redirecting —
                        // landing somewhere you did not ask for is worse than a
                        // wall of zeroes, but so is the wall.
                        <Panel><PanelBody>
                            <EmptyState
                                message={`Nothing spent in ${monthLabel(year, month)}`}
                                hint={`The most recent month with spending is ${
                                    monthLabel(
                                        Number(data.latestMonthWithSpending.slice(0, 4)),
                                        Number(data.latestMonthWithSpending.slice(5, 7)),
                                    )}.`}
                                action={
                                    <button
                                        type="button"
                                        onClick={() => {
                                            const m = data.latestMonthWithSpending!;
                                            setMonth({
                                                year: Number(m.slice(0, 4)),
                                                month: Number(m.slice(5, 7)),
                                            });
                                            setExpandedIds(new Set());
                                            setDetailId(null);
        setDetailId(null);
                                        }}
                                        className="h-control-28px rounded border border-border px-3 text-xs font-medium text-accent hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                    >
                                        Go to{' '}
                                        {monthLabel(
                                            Number(data.latestMonthWithSpending.slice(0, 4)),
                                            Number(data.latestMonthWithSpending.slice(5, 7)),
                                        )}
                                    </button>
                                }
                            />
                        </PanelBody></Panel>
                    ) : !data || data.rows.length === 0 ? (
                        <Panel><PanelBody>
                            <EmptyState
                                message={`Nothing spent in ${monthLabel(year, month)}`}
                                hint="Categorised spending shows up here as soon as there is some."
                            />
                        </PanelBody></Panel>
                    ) : (
                        <>
                            <Panel>
                                <PanelHead className="flex items-center justify-between">
                                    <span className="font-medium">All categories</span>
                                    <span className="text-xs text-text-muted">
                                        shaded band = your last {data.windowMonths} months, same days
                                    </span>
                                </PanelHead>
                                <PanelBody>
                                    {chart ? (
                                        <BudgetHero
                                            series={chart.series}
                                            normal={chart.normal}
                                            daysInMonth={data.daysInMonth}
                                            elapsedDays={data.elapsedDays}
                                            ceiling={chart.ceiling}
                                            currency={currency}
                                        />
                                    ) : null}
                                </PanelBody>
                            </Panel>

                            <Panel>
                                <PanelHead className="flex flex-wrap items-center justify-between gap-2">
                                    <span className="font-medium">By category</span>
                                    <span className="flex items-center gap-2">
                                        <span className="text-xs text-text-muted">
                                            unset = average of the {data.windowMonths} months before {monthLabel(year, month)}
                                        </span>
                                        {/* Whole-table, month-scoped actions belong in the
                                            panel head rather than on a row (ADR-0021 Rule 10:
                                            a per-row menu obligates a visible affordance on
                                            every row at all three densities). */}
                                        <button
                                            type="button"
                                            disabled={fillMut.isPending}
                                            onClick={() => (monthHasTargets
                                                ? setPendingFill('copy')
                                                : void runFill('copy'))}
                                            className="h-control-28px rounded border border-border px-2 text-xs hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-40"
                                        >
                                            Copy last month
                                        </button>
                                        <button
                                            type="button"
                                            disabled={fillMut.isPending}
                                            onClick={() => (monthHasTargets
                                                ? setPendingFill('average')
                                                : void runFill('average'))}
                                            className="h-control-28px rounded border border-border px-2 text-xs hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-40"
                                        >
                                            Fill from {data.windowMonths}-month average
                                        </button>
                                    </span>
                                </PanelHead>
                                <PanelBody>
                                    {notice !== null ? (
                                        <p role="status" className="border-b border-border/40 pb-1.5 text-[0.6875rem] text-text-muted">
                                            {notice}
                                        </p>
                                    ) : null}
                                    {data.mixedCurrency ? (
                                        <p className="border-b border-border/40 pb-1.5 text-[0.6875rem] text-state-warning">
                                            This ledger holds more than one currency; totals are summed without conversion.
                                        </p>
                                    ) : null}
                                    {/* The hero is an aggregate and can look calm while one
                                        category runs hot and another runs cool, cancelling
                                        out. Saying so in words is cheaper than a chart that
                                        cannot. */}
                                    {hot.length > 0 ? (
                                        <p className="border-b border-border/40 pb-1.5 text-[0.6875rem] text-text-muted">
                                            <span className="font-medium text-state-danger">
                                                {hot.length} {hot.length === 1 ? 'category is' : 'categories are'} over target
                                            </span>
                                            {' — '}
                                            {hot.slice(0, 3).map((r) => accountPaths.get(r.categoryId) ?? r.name).join(', ')}
                                            {hot.length > 3 ? ` and ${hot.length - 3} more` : ''}.
                                        </p>
                                    ) : null}
                                    <CategoryRows
                                        rows={data.rows}
                                        accountPaths={accountPaths}
                                        currency={currency}
                                        ledgerId={ledgerId}
                                        monthKey={monthKey}
                                        expandedIds={expandedIds}
                                        onToggle={toggleExpanded}
                                        detailId={detailId}
                                        onToggleDetail={toggleDetail}
                                        onSetTarget={(categoryId, amount) =>
                                            targetMut.mutate({ categoryId, amount })}
                                        saving={targetMut.isPending}
                                    />
                                    <div className="flex items-center justify-between gap-2 border-t border-border pt-2 text-sm">
                                        <span className="font-medium">Total</span>
                                        <span className="flex items-baseline gap-3">
                                            <span className="font-mono font-semibold tabular-nums">
                                                {formatCurrency(data.actualTotal, currency)}
                                            </span>
                                            <span className="font-mono text-xs tabular-nums text-text-muted">
                                                {data.typicalTotal === null
                                                    ? 'no history'
                                                    : `usually ${formatCurrency(data.typicalTotal, currency)}`}
                                            </span>
                                        </span>
                                    </div>
                                </PanelBody>
                            </Panel>
                        </>
                    )}
                </div>
            </MainPane>

            {/* Confirmed only when the month ALREADY holds targets. ADR-0023
                section E reserves confirmation for destructive content, and one
                click replacing numbers a person typed qualifies; asking on an
                empty month would be a dialog for nothing. */}
            <ConfirmDialog
                open={pendingFill !== null}
                variant="danger"
                title="Replace the targets you have set?"
                body={
                    <>
                        This month already has targets. Filling replaces every one it
                        can with {pendingFill === 'copy' ? "last month's numbers" : 'the average'},
                        and there is no undo.
                    </>
                }
                confirmLabel="Replace"
                isConfirming={fillMut.isPending}
                onConfirm={() => { const k = pendingFill; setPendingFill(null); if (k) void runFill(k); }}
                onCancel={() => setPendingFill(null)}
            />
        </MainArea>
    );
}
