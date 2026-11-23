import { useMemo } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';

import { fetchAccounts, fetchReturnsSeries, specToQuery } from '@/lib/api';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { ExchangeRatesLink, MissingRateNote } from '@/components/ConversionNote';
import { LedgerConversionNote } from '@/components/LedgerConversionNote';
import { SegmentedControl, type SegmentedOption } from '@/components/ui/SegmentedControl';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import type { ReportSpec, ReportViewMode, ReturnsSeriesResult, ReturnsShow } from '@/types/report';

import { BenchmarkPicker } from './BenchmarkPicker';
import { RETURNS_REPORT_NAME } from './catalog';
import { RangePicker } from './RangePicker';
import { Select } from './ReportSelect';
import type { ReportSearch } from './ReportView';
import { ReturnsChart } from './ReturnsChart';
import { ReturnsTable } from './ReturnsTable';
import {
    benchmarkAnchor, benchmarkHistoryNote, describeReturnsSpec, drawableBenchmark, gainFootnote, hasValues, indexFootnote,
    isSinceInception, missingRates, returnsSpecFromSearch, tableFootnote,
} from './returnsModel';
import { SaveReportControl } from './SaveReportControl';

/**
 * The Returns report: the time-weighted index and the cumulative gain over time,
 * for the ledger and each investment account (ADR-0104 D6).
 *
 * The same shell as the aggregation view, and deliberately so. The spec lives
 * in the URL and Save, Save as, Share and pinning are the one mechanism every
 * report uses; what differs is the body, the endpoint, and which controls a
 * report like this one has. A saved or catalog spec reaches this view by its
 * `kind` marker.
 *
 * Every figure comes from the server. The browser draws, formats and labels
 * them; it sums, chains and annualises nothing.
 */

const VIEW_OPTIONS: readonly SegmentedOption<ReportViewMode>[] = [
    { value: 'chart', label: 'Chart' },
    { value: 'table', label: 'Table' },
];
const SHOW_OPTIONS: readonly SegmentedOption<ReturnsShow>[] = [
    { value: 'index', label: 'Index' },
    { value: 'gain', label: 'Gain' },
];
/** No day: a returns point is a month, quarter or year end. */
const INTERVALS = ['Month', 'Quarter', 'Year'] as const;

const ALL_PLUS_EACH = 'all-plus-each';
const LEDGER_ONLY = 'ledger';

/** The account control's value for a spec. */
function accountChoice(spec: ReportSpec): string {
    if (spec.accountId) return spec.accountId;
    return spec.includeAccounts === false ? LEDGER_ONLY : ALL_PLUS_EACH;
}

export function ReturnsReportView() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const search = useSearch({ strict: false }) as ReportSearch;
    const navigate = useNavigate();

    const spec = useMemo(() => returnsSpecFromSearch(search), [search]);
    const title = search.name ?? RETURNS_REPORT_NAME;

    const patch = (next: Partial<ReportSpec>) => {
        void navigate({
            to: '/ledgers/$ledgerId/reports/view',
            params: { ledgerId },
            search: { ...search, kind: 'returns', ...next },
            replace: true,
        });
    };

    const query = useQuery({
        queryKey: ['returns-series', ledgerId, specToQuery(spec)],
        queryFn: () => fetchReturnsSeries(ledgerId, spec),
        // Changing a control keeps the bar in place while the body says Running.
        placeholderData: keepPreviousData,
    });
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
    });
    const brokerages = (accountsQuery.data ?? []).filter((a) => a.accountType === 'investment');
    const { ledger, canEdit } = useLedgerRole(ledgerId);

    const result: ReturnsSeriesResult | undefined = query.data;
    const running = query.isPending || query.isPlaceholderData;
    const series = result?.series ?? [];
    const interval = result?.interval ?? (spec.bucket === 'Year' ? 'year' : spec.bucket === 'Quarter' ? 'quarter' : 'month');
    const anyValues = series.some((s) => hasValues(s, 'index'));
    const show = spec.returnsShow ?? 'index';
    const chart = spec.view !== 'table';
    const gainChart = chart && show === 'gain';
    const benchmark = result?.benchmark ?? null;
    // The benchmark shows on the Index chart and in the table, never on Gain.
    const benchmarkShown = !gainChart && drawableBenchmark(benchmark);
    // The chart names the rates a line it draws is missing; the table, every rate a cell
    // needs, the money-weighted one's from the year before the range included.
    const missing = missingRates(series, benchmarkShown, chart ? 'drawn' : 'all');
    const seriesMissing = missingRates(series, false, 'drawn');
    // The line the chart draws the benchmark against.
    const anchor = benchmarkAnchor(series);
    // Its currency, for the picker: the drawn line's, else, before this spec's series
    // answers, the account's the report names or the reporting currency. The previous
    // spec's series, kept on screen while this one runs, is not the line to be drawn.
    const seriesCurrency = (query.isPlaceholderData ? undefined : anchor?.currencyCode)
        ?? (spec.accountId ? brokerages.find((a) => a.id === spec.accountId)?.currencyCode : ledger?.currencyCode);
    const historyNote = show === 'index' && benchmarkShown ? benchmarkHistoryNote(anchor, benchmark.symbol) : null;
    const ratesLink = <ExchangeRatesLink ledgerId={ledgerId} />;

    const choices = [ALL_PLUS_EACH, LEDGER_ONLY, ...brokerages.map((a) => a.id)];
    const choice = accountChoice(spec);
    // A saved account the list has not named yet (or no longer holds) is still the choice.
    const options = choices.includes(choice) ? choices : [...choices, choice];
    const labels: Record<string, string> = {
        [ALL_PLUS_EACH]: 'All, plus each',
        [LEDGER_ONLY]: 'All accounts',
        ...Object.fromEntries(brokerages.map((a) => [a.id, a.name])),
    };
    if (!(choice in labels)) labels[choice] = 'Account';

    const subtitle = describeReturnsSpec(spec, chart);

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: 'Reports',
                            node: (
                                <Link to="/ledgers/$ledgerId/reports" params={{ ledgerId }} className="hover:underline">
                                    Reports
                                </Link>
                            ),
                        },
                        { label: title },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="flex h-full min-w-0 flex-col p-5">
                    <div className="flex flex-wrap items-start justify-between gap-2">
                        <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
                        <div className="flex items-start gap-1.5">
                            <SaveReportControl
                                ledgerId={ledgerId}
                                spec={spec}
                                savedId={search.saved}
                                savedName={search.name}
                                onSaved={(report) => {
                                    void navigate({
                                        to: '/ledgers/$ledgerId/reports/view',
                                        params: { ledgerId },
                                        search: { ...search, kind: 'returns', saved: report.id, name: report.name },
                                        replace: true,
                                    });
                                }}
                            />
                        </div>
                    </div>
                    <p className="mb-3 text-sm text-text-muted">{subtitle}</p>
                    {/* The ledger's line converts its brokerages into the reporting
                        currency (ADR-0114 D7); each account's line stays in its own. */}
                    <LedgerConversionNote ledgerId={ledgerId} scope="brokerages" className="-mt-2 mb-3" />

                    <div className="mb-3 flex flex-wrap items-end gap-1.5">
                        <SegmentedControl
                            label="View"
                            options={VIEW_OPTIONS}
                            value={chart ? 'chart' : 'table'}
                            onChange={(v) => patch({ view: v })}
                        />
                        {chart && anyValues ? (
                            <SegmentedControl
                                label="Show"
                                options={SHOW_OPTIONS}
                                value={show}
                                onChange={(v) => patch({ returnsShow: v })}
                            />
                        ) : null}
                        <Select label="Period" value={spec.bucket} options={INTERVALS}
                            onChange={(v) => patch({ bucket: v as ReportSpec['bucket'] })} />
                        <RangePicker spec={spec} onChange={patch} inception />
                        <Select label="Accounts" value={choice} options={options} labels={labels}
                            onChange={(v) => patch(
                                v === ALL_PLUS_EACH ? { accountId: undefined, includeAccounts: true }
                                    : v === LEDGER_ONLY ? { accountId: undefined, includeAccounts: false }
                                        : { accountId: v, includeAccounts: false },
                            )} />
                        {/* Hidden on Gain, as Show hides in Table; the pick stays in the spec. */}
                        {gainChart ? null : (
                            <BenchmarkPicker
                                ledgerId={ledgerId}
                                value={spec.benchmark ?? undefined}
                                onChange={(symbol) => patch({ benchmark: symbol })}
                                canEdit={canEdit}
                                seriesCurrency={seriesCurrency}
                            />
                        )}
                    </div>

                    {running ? (
                        <p className="text-xs text-text-muted">Running…</p>
                    ) : query.isError ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {(query.error as Error).message}
                        </p>
                    ) : !anyValues && seriesMissing.length > 0 ? (
                        // Nothing drawn because a rate is missing, not because nothing is
                        // invested: once a rate is unknown every later point needs it too.
                        <MissingRateNote className="mt-2" missing={seriesMissing} ratesLink={ratesLink} />
                    ) : !anyValues ? (
                        <div className="px-4 py-10 text-center text-text-muted">
                            <h2 className="mb-1.5 text-sm font-semibold text-text">No invested balance in this window</h2>
                            <p className="text-xs">
                                Returns need money held in an investment account. Widen the window, or choose Since inception.
                            </p>
                        </div>
                    ) : chart ? (
                        <div className="flex min-h-0 flex-1 flex-col pb-2">
                            <ReturnsChart series={series} show={show} interval={interval} benchmark={benchmark} />
                            {/* A point that needs a missing rate is a break in its line; this says which rate. */}
                            <MissingRateNote className="mt-2" missing={missing} ratesLink={ratesLink} />
                            {historyNote ? <p className="mt-2 text-xs text-text-muted">{historyNote}</p> : null}
                            <p className="mt-3 max-w-prose text-[0.6875rem] leading-[1.4] text-text-muted">
                                {show === 'index'
                                    ? indexFootnote(benchmark, anchor ? [anchor.currencyCode] : [])
                                    : gainFootnote(benchmark, spec.benchmark)}
                            </p>
                        </div>
                    ) : (
                        <>
                            <ReturnsTable
                                series={series}
                                benchmark={benchmark}
                                sinceInception={isSinceInception(spec)}
                                scrollClassName="min-h-0 flex-1 overflow-auto"
                            />
                            <MissingRateNote className="mt-2" missing={missing} ratesLink={ratesLink} />
                            <p className="mt-2 text-[0.6875rem] leading-[1.4] text-text-muted">
                                {tableFootnote(benchmark, series.map((s) => s.currencyCode))}
                            </p>
                        </>
                    )}
                </div>
            </MainPane>
        </MainArea>
    );
}
