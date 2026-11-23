import { rateUnavailableTitle } from '@/lib/conversion';
import { formatLedgerDateLong } from '@/lib/dates';
import type {
    ReportSpec, ReturnsBenchmark, ReturnsReason, ReturnsSeries, ReturnsSeriesPoint, ReturnsShow,
} from '@/types/report';

import { describeRange } from './RangePicker';
import type { ReportSearch } from './ReportView';

/**
 * The Returns report's spec and the plain words for its figures.
 *
 * Every figure here arrives derived from the server. This module formats and
 * labels them; it adds, chains and annualises nothing.
 */

/**
 * A benchmark symbol's shape, as the server checks it (`BenchmarkSymbol`): one to
 * 32 of A–Z, 0–9 and `. - ^ = _`. A value of any other shape would be a 400 the page
 * cannot explain, so it is dropped, as the route drops every unknown value.
 */
const SYMBOL_SHAPE = /^[A-Z0-9.\-^=_]{1,32}$/;

export function benchmarkSymbol(value: unknown): string | undefined {
    return typeof value === 'string' && SYMBOL_SHAPE.test(value) ? value : undefined;
}

/** A benchmark as the picker, the legend and the footnote name it: "Total World Fund
 *  (VT)", or the bare symbol when the fund has no name of its own. */
export function benchmarkLabel(name: string | null | undefined, symbol: string): string {
    return name && name !== symbol ? `${name} (${symbol})` : symbol;
}

/** Whether a result has a benchmark to draw: one asked for and still the ledger's. */
export function drawableBenchmark(
    b: ReturnsBenchmark | null | undefined,
): b is ReturnsBenchmark & { unavailableReason: null } {
    return b != null && b.unavailableReason === null;
}

/** Whether a returns spec runs since inception: no sliding window and no start date. */
export function isSinceInception(spec: Pick<ReportSpec, 'dateMode' | 'from'>): boolean {
    return spec.dateMode !== 'Sliding' && !spec.from;
}

/** Fill the gaps so the page always has a complete, runnable returns spec. */
export function returnsSpecFromSearch(search: ReportSearch): ReportSpec {
    const bucket = search.bucket === 'Quarter' || search.bucket === 'Year' ? search.bucket : 'Month';
    return {
        kind: 'returns',
        // Inert here; the type requires them.
        measure: 'Net',
        groupBy: 'Account',
        bucket,
        // Since inception unless the URL says a window.
        dateMode: search.dateMode ?? 'Static',
        windowCount: search.dateMode === 'Sliding' ? (search.windowCount ?? 12) : undefined,
        windowUnit: search.dateMode === 'Sliding' ? (search.windowUnit ?? 'Month') : undefined,
        windowAlign: search.dateMode === 'Sliding' ? (search.windowAlign ?? true) : undefined,
        from: search.from,
        to: search.to,
        accountId: search.accountId,
        includeAccounts: search.includeAccounts ?? true,
        returnsShow: search.returnsShow ?? 'index',
        // The chart is the report's face; the table is its counterpart.
        view: search.view ?? 'chart',
        // Kept whatever the view: Gain hides the control and keeps the pick (ADR-0114 D1).
        benchmark: benchmarkSymbol(search.benchmark),
    };
}

/**
 * What a returns spec shows, as a saved card and the view's subtitle say it. The pick
 * is named ("vs VT") wherever it is compared: on Index and in the table, not on Gain,
 * which draws no benchmark.
 */
export function describeReturnsSpec(spec: ReportSpec, chartMeasure = true): string {
    const gain = chartMeasure && spec.returnsShow === 'gain';
    const first = chartMeasure ? (gain ? 'Gain' : 'Index') : 'Table';
    const vs = spec.benchmark && !gain ? ` · vs ${spec.benchmark}` : '';
    return `${first} · ${spec.bucket.toLowerCase()}ly · ${describeRange(spec).toLowerCase()}${vs}`;
}

/** Whether a spec is the Returns report. A spec with no marker is the aggregation. */
export const isReturnsSpec = (spec: Pick<ReportSpec, 'kind'>): boolean => spec.kind === 'returns';

/** The short reason, for a cell with no room for a sentence. */
const SHORT: Readonly<Record<ReturnsReason, string>> = {
    before_first_funding: 'not invested yet',
    no_invested_base: 'no invested balance',
    negative_cumulative_growth: 'holdings worth less than nothing',
    rate_unavailable: 'no exchange rate',
    benchmark_not_in_ledger: 'benchmark removed',
    benchmark_unavailable: 'no benchmark history',
    prior_index_zero: 'prior index is zero',
    under_a_year_covered: 'under a year covered',
    too_few_flows: 'too few dated flows',
    single_signed_flows: 'all flows run one way',
    zero_length_window: 'all flows on one day',
    indeterminate: 'flows offset exactly',
    no_root_in_range: 'no rate fits the flows',
};

/** The reason as a sentence, for the Note column and a tooltip. */
const SENTENCE: Readonly<Record<ReturnsReason, string>> = {
    before_first_funding: 'Nothing is invested yet, so there is no return to measure.',
    no_invested_base: 'No invested balance at this point; the index resumes from its last value when money returns.',
    negative_cumulative_growth: 'The holdings were worth less than nothing, and growth below zero is not an index.',
    rate_unavailable: 'A figure here needs an exchange rate the ledger does not hold.',
    benchmark_not_in_ledger: 'The benchmark is no longer one of this ledger’s benchmarks.',
    benchmark_unavailable: 'The benchmark has no stored history for this point.',
    prior_index_zero: 'The previous index is zero (a total loss), so a return from it has no base.',
    under_a_year_covered: 'Nothing was invested when the 12 months ending here began, so a yearly rate would magnify a shorter holding.',
    too_few_flows: 'Fewer than two dated flows, so no rate can be solved.',
    single_signed_flows: 'Every flow ran one way, so no rate reconciles them.',
    zero_length_window: 'Every flow falls on one day, so there is no time to annualise over.',
    indeterminate: 'The flows offset exactly, so every rate fits them equally.',
    no_root_in_range: 'No rate between -99.99% and 10000% a year fits the flows.',
};

const known = (r: string | null | undefined): r is ReturnsReason => r != null && r in SHORT;

/** The short reason; an unknown code is shown as it came, never swallowed. */
export function reasonShort(r: string | null | undefined): string {
    if (r == null) return '';
    return known(r) ? SHORT[r] : r.replace(/_/g, ' ');
}

export function reasonSentence(r: string | null | undefined): string {
    if (r == null) return '';
    return known(r) ? SENTENCE[r] : r.replace(/_/g, ' ');
}

/**
 * Why a point's index is null, as the Note column says it: a missing rate names each
 * pair and day it needed, when the server listed them.
 */
export function indexReasonSentence(p: ReturnsSeriesPoint): string {
    if (p.indexUnavailableReason === 'rate_unavailable' && p.rateUnavailable.length > 0) {
        return rateUnavailableTitle(p.rateUnavailable);
    }
    return reasonSentence(p.indexUnavailableReason);
}

/**
 * Why a point's benchmark is null when the point's own index is not: the benchmark's
 * own reason, in words. Empty when there is a benchmark value, or when the index is
 * null too, because then the index's reason is the one that says why (the benchmark
 * pauses with it, ADR-0114 D3).
 */
export function benchmarkGapSentence(p: ReturnsSeriesPoint, symbol: string): string {
    if (p.benchmark !== null || p.index === null) return '';
    switch (p.benchmarkUnavailableReason) {
        case 'benchmark_unavailable':
            return p.benchmarkUnavailableDate
                ? `No stored ${symbol} history for ${formatLedgerDateLong(p.benchmarkUnavailableDate)}.`
                : `No stored ${symbol} history for this point.`;
        case 'rate_unavailable':
            return p.benchmarkRateUnavailable.length > 0
                ? rateUnavailableTitle(p.benchmarkRateUnavailable)
                : reasonSentence('rate_unavailable');
        default:
            return reasonSentence(p.benchmarkUnavailableReason);
    }
}

/**
 * The sentence a footnote adds when the benchmark's closes are converted into the
 * currency of a line it is compared with (ADR-0114 D3, unhedged). `against` are the
 * currencies of those lines.
 */
function convertedSentence(benchmark: ReturnsBenchmark, against: readonly string[]): string {
    const from = benchmark.currencyCode;
    if (!from) return '';
    const all = [...new Set(against)];
    const into = all.filter((c) => c !== from).sort();
    if (into.length === 0) return '';
    if (all.length === 1) return ` Its closes are in ${from}, converted into ${into[0]} at each day’s rate.`;
    return ` Its closes are in ${from}; for a line in ${into.join(' or ')} they are converted into that currency at each day’s rate.`;
}

const INDEX_BASE = 'Index is the time-weighted return from the start of the range, or from the first money invested in it, set to 100. It is not annualised.';

/** The sentence a footnote gives a picked symbol that is no longer the ledger's (ADR-0114 D1). */
const removedSentence = (symbol: string, what: string) =>
    ` ${symbol} is no longer one of this ledger’s benchmarks, so ${what}. It comes back if ${symbol} is added again in Settings → Quotes.`;

/**
 * The Index chart's footnote. With a benchmark it names the fund, says the line is
 * its total return set to 100 at the same start, and that it pauses with the
 * portfolio (ADR-0114 D3); a picked symbol the ledger no longer holds is said to be
 * so (ADR-0114 D1). `against` is the currency of the line the benchmark is drawn against.
 */
export function indexFootnote(benchmark: ReturnsBenchmark | null, against: readonly string[]): string {
    if (benchmark === null) return `${INDEX_BASE} Not benchmarked.`;
    if (!drawableBenchmark(benchmark)) return INDEX_BASE + removedSentence(benchmark.symbol, 'no benchmark is drawn');
    const label = benchmarkLabel(benchmark.name, benchmark.symbol);
    return `${INDEX_BASE} The benchmark, ${label}, is a total return (the fund’s dividend-adjusted close), `
        + 'set to 100 at the same start. While nothing is invested it pauses too, and it resumes from its own last value.'
        + convertedSentence(benchmark, against);
}

/**
 * The Gain chart's footnote. A picked benchmark draws nothing here, because an index
 * has no gain in money; the pick is kept and the footnote says why there is no line.
 */
export function gainFootnote(benchmark: ReturnsBenchmark | null, picked: string | null | undefined): string {
    const base = 'Gain is value now, minus value at the start, minus money added since: the money-weighted picture '
        + 'in money, the ledger’s in its reporting currency and each account’s in its own.';
    if (!picked) return base;
    // A pick the ledger no longer holds says so here too, as on Index and in the table (ADR-0114 D1).
    if (benchmark !== null && !drawableBenchmark(benchmark)) return base + removedSentence(benchmark.symbol, 'no benchmark is drawn');
    const label = benchmark ? benchmarkLabel(benchmark.name, benchmark.symbol) : picked;
    return `${base} The benchmark, ${label}, is an index with no gain in money, so it is drawn on Index only.`;
}

/** The table's footnote, with the sentence the benchmark column needs. `against` are
 *  the currencies of the table's blocks. */
export function tableFootnote(benchmark: ReturnsBenchmark | null, against: readonly string[]): string {
    const base = 'The server derives period return from the index; the browser sums nothing. '
        + 'Money-weighted is the return of the 12 months ending at each row, including any before the range.';
    if (benchmark === null) return base;
    if (!drawableBenchmark(benchmark)) return base + removedSentence(benchmark.symbol, 'it has no column');
    const label = benchmarkLabel(benchmark.name, benchmark.symbol);
    return `${base} ${benchmark.symbol} index is ${label} as a total return, set to 100 at each block’s first invested point.`
        + convertedSentence(benchmark, against);
}

/**
 * The series the chart draws the one benchmark line against: the ledger's when the
 * result has it, else the one account's (ADR-0114 D3: the server rebases each
 * series' own benchmark, and the chart shows one).
 */
export function benchmarkAnchor(series: readonly ReturnsSeries[]): ReturnsSeries | undefined {
    return ledgerSeries(series) ?? accountSeries(series)[0];
}

/**
 * What the chart says when the benchmark has no stored history for a point its line
 * needs (`benchmark_unavailable`, ADR-0114 D3): the first such day, and whether the
 * line only breaks there or is not drawn at all. Null when nothing is missing. A
 * missing rate is said by the missing-rate note instead.
 */
export function benchmarkHistoryNote(anchor: ReturnsSeries | undefined, symbol: string): string | null {
    if (!anchor) return null;
    const invested = anchor.points.filter((p) => p.index !== null);
    const gaps = invested.filter((p) => p.benchmark === null && p.benchmarkUnavailableReason === 'benchmark_unavailable');
    if (gaps.length === 0) return null;
    const days = gaps.flatMap((p) => (p.benchmarkUnavailableDate ? [p.benchmarkUnavailableDate] : [])).sort();
    const others = gaps.length - 1;
    const more = others === 0 ? '' : ` and ${others} other ${others === 1 ? 'point' : 'points'}`;
    const when = days.length > 0 ? `for ${formatLedgerDateLong(days[0]!)}${more}` : 'for some points';
    const none = invested.every((p) => p.benchmark === null);
    return `No ${symbol} history is stored ${when}, so ${none ? 'no benchmark line is drawn' : 'the benchmark line breaks there'}.`;
}

/**
 * Every rate the shown figures needed and the ledger does not hold: each point's own,
 * and with a benchmark shown, the benchmark's conversion too.
 *
 * `drawn` keeps a point's own rates only where they left a line's figure (index or gain)
 * empty: a rate missing only for the year before the range leaves just the money-weighted
 * figure null, which the chart does not draw, so it is the table's to name (`all`).
 */
export function missingRates(
    series: readonly ReturnsSeries[], withBenchmark: boolean, figures: 'drawn' | 'all',
): ReturnsSeriesPoint['rateUnavailable'] {
    const broken = (p: ReturnsSeriesPoint) => figures === 'all'
        || p.indexUnavailableReason === 'rate_unavailable' || p.gainUnavailableReason === 'rate_unavailable';
    return series.flatMap((s) => s.points.flatMap((p) => [
        ...(broken(p) ? p.rateUnavailable : []),
        ...(withBenchmark ? p.benchmarkRateUnavailable : []),
    ]));
}

/** What a series is called on the chart and in the table. */
export const seriesName = (s: ReturnsSeries): string =>
    s.scope === 'ledger' ? 'All accounts (ledger)' : (s.accountName ?? 'Account');

/** The ledger-wide series, if the result has one. */
export function ledgerSeries(series: readonly ReturnsSeries[]): ReturnsSeries | undefined {
    return series.find((s) => s.scope === 'ledger');
}

export function accountSeries(series: readonly ReturnsSeries[]): ReturnsSeries[] {
    return series.filter((s) => s.scope === 'account');
}

/** A series' drawable value for the chart's measure; null is a break, never a zero. */
export function valueOf(point: ReturnsSeriesPoint, show: ReturnsShow): number | null {
    return show === 'index' ? point.index : point.gain;
}

/** Whether a series has any value to draw for the measure. */
export function hasValues(s: ReturnsSeries, show: ReturnsShow): boolean {
    return s.points.some((p) => valueOf(p, show) !== null);
}

/** `YYYY-MM`, `YYYY-Q#` or `YYYY`, read off the server's period end; the date part is not re-zoned. */
export function periodLabel(periodEnd: string, interval: 'month' | 'quarter' | 'year'): string {
    const [y, m] = periodEnd.slice(0, 10).split('-');
    if (interval === 'year') return y!;
    if (interval === 'quarter') return `${y}-Q${Math.floor((Number(m) - 1) / 3) + 1}`;
    return `${y}-${m}`;
}

/**
 * The chart's period labels, one per column, so they read evenly.
 *
 * Month and quarter charts are labelled by year, at the first column of each
 * year, so every label is one fixed step from the next. The axis draws a label
 * at its own column and thins them by width, and a blank column draws nothing,
 * so the last period is never appended at an odd distance. Fewer than two year
 * starts in the window leave nothing to space by year, so the period labels
 * stand and the axis thins them as it always has.
 */
export function axisLabelsFor(periodEnds: readonly string[], interval: 'month' | 'quarter' | 'year'): string[] {
    const periods = periodEnds.map((p) => periodLabel(p, interval));
    if (interval === 'year') return periods;
    const startOfYear = interval === 'month' ? /-01$/ : /-Q1$/;
    const starts = periods.flatMap((l, i) => (startOfYear.test(l) ? [i] : []));
    if (starts.length < 2) return periods;
    return periods.map((l, i) => (starts.includes(i) ? l.slice(0, 4) : ''));
}

/**
 * Push end labels apart so two close end values stay readable; only their
 * position moves. `gap` is at least a label's height, in percent of the plot. A
 * stack pushed past `limit` is moved back up, keeping the gap.
 */
export function spreadLabels(tops: readonly number[], gap: number, limit = 97): number[] {
    const order = tops.map((t, i) => ({ t, i })).sort((a, b) => a.t - b.t);
    for (let k = 1; k < order.length; k++) {
        if (order[k]!.t - order[k - 1]!.t < gap) order[k]!.t = order[k - 1]!.t + gap;
    }
    if (order.length > 0 && order[order.length - 1]!.t > limit) {
        order[order.length - 1]!.t = limit;
        for (let k = order.length - 2; k >= 0; k--) {
            if (order[k + 1]!.t - order[k]!.t < gap) order[k]!.t = order[k + 1]!.t - gap;
        }
    }
    const out = new Array<number>(tops.length);
    for (const o of order) out[o.i] = o.t;
    return out;
}

const MINUS = '−';

/** A decimal fraction as a signed percentage, to one place. */
export function formatFraction(f: number): string {
    const pct = Math.round(f * 1000) / 10;
    return `${pct < 0 ? MINUS : pct > 0 ? '+' : ''}${Math.abs(pct).toFixed(1)}%`;
}

/** An unsigned annualised rate as a percentage, to one place. */
export function formatRate(f: number): string {
    const pct = Math.round(f * 1000) / 10;
    return `${pct < 0 ? MINUS : ''}${Math.abs(pct).toFixed(1)}%`;
}

/** An index on base 100, to one decimal in the table. */
export const formatIndex = (v: number): string => v.toFixed(1);
