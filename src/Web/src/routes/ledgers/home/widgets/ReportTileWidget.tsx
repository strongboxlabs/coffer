import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { fetchReportSummary, fetchReturnsSeries, specToQuery } from '@/lib/api';
import { cn } from '@/lib/cn';
import type { ReportGroupBy, ReportSpec } from '@/types/report';

import { chartTopN } from '../../reports/chartSeries';
import { describeRange } from '../../reports/RangePicker';
import { crossCurrencyChartText, currencyRules } from '../../reports/reportCurrency';
import { ReportChart } from '../../reports/ReportChart';
import { ReportTable } from '../../reports/ReportTable';
import { ReturnsChart } from '../../reports/ReturnsChart';
import { ReturnsTable } from '../../reports/ReturnsTable';
import { hasValues, isSinceInception, missingRates, returnsSpecFromSearch } from '../../reports/returnsModel';
import { chartRowsOf, periodsOf, pivot, specFromSearch } from '../../reports/ReportView';
import { NO_SYSTEM_IDS, useChartSystemIds } from '../../reports/useChartSystemIds';
import { parseReportTileId } from '../dashboardCatalog';
import { useHome, useWidgetFrame } from '../homeContext';
import { ExchangeRatesLink, MissingRateNote } from '@/components/ConversionNote';

import { Money } from '../Money';
import { resolveTile, useSavedReports, type TileReport } from '../reportTiles';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

/** The tile's fixed heights (ADR-0104 D2, as amended): chart or table, and its legend, fit inside. */
const HEIGHT = { compact: 'h-fixed-176px', tall: 'h-fixed-256px' } as const;

/**
 * One saved or catalog report on the home screen, on its own window, at a fixed
 * height, as a chart or a table. "Open report →" is its only control: a widget is a
 * fixed answer, and a question to adjust belongs in Reports (ADR-0104). The tile's id
 * names the report; one that no longer exists is not drawn, and while the layout is
 * edited it says so and can be removed.
 */
export function ReportTileWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const ref = parseReportTileId(frame.widget.id);
    const saved = useSavedReports(home.ledgerId, ref?.kind === 'saved');
    const resolution = resolveTile(frame.widget.id, saved.data);
    const height = HEIGHT[frame.settings.height === 'tall' ? 'tall' : 'compact'];

    if (resolution.state === 'unknown') {
        if (saved.isError) {
            return (
                <WidgetPanel title="Report">
                    <WidgetError what="saved reports" error={saved.error} onRetry={() => void saved.refetch()} />
                </WidgetPanel>
            );
        }
        return (
            <WidgetPanel title="Report">
                <div aria-busy="true" aria-label="Loading the report" className="p-4"><Skeleton className={height} /></div>
            </WidgetPanel>
        );
    }
    if (resolution.state === 'missing') {
        // Not drawn outside the edit: a tile with nothing to show is not an answer.
        if (!home.editing) return null;
        return (
            <WidgetPanel title="Report">
                <p className="flex flex-wrap items-center gap-x-1 p-4 text-sm text-text-muted">
                    Report no longer exists ·
                    {/* A change to the layout, not the tile's content: usable while
                        the rest of the tile is inert (WidgetHost). */}
                    <Button variant="link" size="xs" data-edit-control pending={home.layoutSaving}
                        onClick={() => home.remove(frame.widget.id)}>Remove</Button>
                </p>
            </WidgetPanel>
        );
    }
    return <ReportTile report={resolution.report} height={height} />;
}

function ReportTile({ report, height }: { report: TileReport; height: string }) {
    // The spec's marker picks the report; none is the aggregation.
    return report.spec.kind === 'returns'
        ? <ReturnsTile report={report} height={height} />
        : <AggregationTile report={report} height={height} />;
}

/** The Returns report on its own window: its chart (the index or the gain), or its table. */
function ReturnsTile({ report, height }: { report: TileReport; height: string }) {
    const home = useHome();
    const frame = useWidgetFrame();
    const spec = returnsSpecFromSearch(report.spec);
    const chart = frame.settings.view !== 'table';
    const query = useQuery({
        queryKey: ['home', home.ledgerId, 'returns-series', specToQuery(spec), home.today],
        queryFn: () => fetchReturnsSeries(home.ledgerId, spec),
    });
    const link = (
        <Link
            to="/ledgers/$ledgerId/reports/view"
            params={{ ledgerId: home.ledgerId }}
            search={report.search}
            className="text-xs font-medium text-accent hover:underline"
        >
            Open report →
        </Link>
    );
    const title = <>{report.name} <span className="font-normal text-text-muted">· {describeRange(spec).toLowerCase()}</span></>;

    if (query.isError) {
        return <WidgetPanel title={title} link={link}><WidgetError what={report.name} error={query.error} onRetry={() => void query.refetch()} /></WidgetPanel>;
    }
    if (!query.data) {
        return (
            <WidgetPanel title={title} link={link}>
                <div aria-busy="true" aria-label={`Loading ${report.name}`} className="p-4"><Skeleton className={height} /></div>
            </WidgetPanel>
        );
    }
    const result = query.data;
    const anyValues = result.series.some((s) => hasValues(s, 'index'));
    // Nothing to draw for a missing rate is not "nothing invested": say which rate.
    const missing = missingRates(result.series, false, 'drawn');
    let body;
    if (!anyValues && missing.length > 0) {
        body = <MissingRateNote missing={missing} ratesLink={<ExchangeRatesLink ledgerId={home.ledgerId} />} />;
    } else if (!anyValues) {
        body = <p className="text-sm text-text-muted">No invested balance in this window.</p>;
    } else if (chart) {
        body = <ReturnsChart series={result.series} show={spec.returnsShow ?? 'index'} interval={result.interval} benchmark={result.benchmark} fit />;
    } else {
        body = (
            <ReturnsTable
                series={result.series}
                benchmark={result.benchmark}
                sinceInception={isSinceInception(spec)}
                scrollClassName="min-h-0 flex-1 overflow-auto"
            />
        );
    }
    return (
        <WidgetPanel title={title} link={link}>
            <div className="p-4">
                <div className={cn('flex flex-col', height)} data-report-tile-body>{body}</div>
            </div>
        </WidgetPanel>
    );
}

function AggregationTile({ report, height }: { report: TileReport; height: string }) {
    const home = useHome();
    const frame = useWidgetFrame();
    const spec = specFromSearch(report.spec);
    const chart = frame.settings.view !== 'table';
    // A chart sends a cut (every group over All) so the server marks the rows it
    // draws; a table sends the spec's own cap. Two requests for two views, as on
    // Reports.
    // Roll-up is a table's subtotals; a chart is always flat (ADR-0104).
    const request: ReportSpec = chart ? { ...spec, topN: chartTopN(spec), rollup: false } : spec;
    const query = useQuery({
        queryKey: ['home', home.ledgerId, 'report', specToQuery(request), home.today],
        queryFn: () => fetchReportSummary(home.ledgerId, request),
    });
    // A category chart waits for the ledger's Uncategorized, as Reports' does.
    const categoryChart = chart && spec.groupBy === 'Category';
    const systemIds = useChartSystemIds(home.ledgerId, categoryChart);

    const link = (
        <Link
            to="/ledgers/$ledgerId/reports/view"
            params={{ ledgerId: home.ledgerId }}
            search={report.search}
            className="text-xs font-medium text-accent hover:underline"
        >
            Open report →
        </Link>
    );
    const title = <>{report.name} <span className="font-normal text-text-muted">· {describeRange(spec).toLowerCase()}</span></>;

    if (query.isError) {
        return <WidgetPanel title={title} link={link}><WidgetError what={report.name} error={query.error} onRetry={() => void query.refetch()} /></WidgetPanel>;
    }
    if (!query.data || (categoryChart && systemIds === null)) {
        return (
            <WidgetPanel title={title} link={link}>
                <div aria-busy="true" aria-label={`Loading ${report.name}`} className="p-4"><Skeleton className={height} /></div>
            </WidgetPanel>
        );
    }

    const result = query.data;
    const periods = periodsOf(result.rows, spec.bucket, result.fromUtc, result.toUtc);
    // A rolled-up parent's own postings are not a group, so the table leaves them out.
    const rows = pivot(result.rows.filter((r) => r.isDirect !== true));
    const { missing, crossCurrency, chartWithheld } = currencyRules(result, spec);
    const chartRows = chart && !chartWithheld ? chartRowsOf(pivot(result.rows)) : null;
    const ranked = spec.bucket === 'None';

    let body;
    if (rows.length === 0) {
        body = <p className="text-sm text-text-muted">Nothing in this range.</p>;
    } else if (chart && crossCurrency !== null) {
        // Accounts in their own currencies beside converted totals: nothing to chart on one scale.
        body = <p className="text-xs text-text-muted">{crossCurrencyChartText(crossCurrency, result.currencyCode)}</p>;
    } else if (chart && chartRows === null) {
        body = <MissingRateNote missing={missing} ratesLink={<ExchangeRatesLink ledgerId={home.ledgerId} />} />;
    } else if (chart) {
        body = (
            <div className="flex h-full min-h-0 flex-col">
                <div className="min-h-0 flex-1">
                    <ReportChart
                        rows={chartRows ?? []}
                        periods={periods}
                        bucketed={!ranked}
                        stack={spec.chartStack ?? 'stacked'}
                        currency={result.currencyCode}
                        groupBy={spec.groupBy}
                        rest={result.rest ?? null}
                        systemCategoryIds={systemIds ?? NO_SYSTEM_IDS}
                        fit
                    />
                </div>
                {ranked ? (
                    // A ranking names each group beside its bar; the server's total
                    // sits under them, never a sum of the bars.
                    <div className="mt-2 flex shrink-0 items-baseline justify-between gap-3 border-t border-border pt-2 text-xs">
                        <span className="text-text-muted">{rankedTotalLabel(spec.groupBy)}</span>
                        <Money amount={result.total} currency={result.currencyCode} tone="fixed" rateUnavailable={result.rateUnavailable} />
                    </div>
                ) : null}
            </div>
        );
    } else {
        body = (
            <div className="flex h-full min-h-0 flex-col">
                <ReportTable spec={spec} result={result} rows={rows} periods={periods} scrollClassName="min-h-0 flex-1 overflow-auto" />
            </div>
        );
    }

    return (
        <WidgetPanel title={title} link={link}>
            <div className="p-4">
                <div className={cn('flex flex-col', height)} data-report-tile-body>{body}</div>
            </div>
        </WidgetPanel>
    );
}

/** The line under a ranking: its total, named for what it adds up. */
function rankedTotalLabel(groupBy: ReportGroupBy): string {
    switch (groupBy) {
        case 'Payee': return 'Total, all payees';
        case 'Category': return 'Total, all categories';
        case 'Account': return 'Total, all accounts';
        case 'Tag': return 'Total, all tags';
        case 'CategoryKind': return 'Total';
    }
}
