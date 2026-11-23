import { cn } from '@/lib/cn';
import { formatCurrency } from '@/lib/money';
import type { RateUnavailable } from '@/lib/types';
import type { ReturnsBenchmark, ReturnsReason, ReturnsSeries, ReturnsSeriesPoint } from '@/types/report';

import { NoRate } from '../home/Money';
import {
    benchmarkGapSentence, benchmarkLabel, drawableBenchmark, formatFraction, formatIndex, formatRate,
    indexReasonSentence, reasonSentence, reasonShort, seriesName,
} from './returnsModel';

const TH = 'sticky top-0 z-10 border-b border-border bg-surface-muted px-2 py-1 text-right text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted';
const TD = 'whitespace-nowrap border-b border-border px-2 py-1 text-right font-mono tabular-nums text-text';
const NIL = 'whitespace-nowrap border-b border-border px-2 py-1 text-right font-mono tabular-nums text-text-subtle';

/**
 * Returns as a table: the verifiable counterpart of the chart. One block per
 * series, a row per period end.
 *
 * Every figure is the server's. The period return is derived there from the
 * index, the money-weighted rate of the 12 months ending at each row is solved
 * there, and this table adds, chains and annualises nothing. A figure the server could not give is a dash,
 * and the reason is said in words beside it: in the money-weighted cell when
 * that is the one missing, in the Note column otherwise. A figure that needs a
 * missing exchange rate reads "no rate", naming the pair and day on hover
 * (ADR-0114 D9).
 *
 * With a benchmark that is the ledger's, a column beside Index carries each
 * block's own benchmark, rebased by the server to that block's first invested
 * point (ADR-0114 D3).
 */
export function ReturnsTable({ series, benchmark = null, sinceInception, scrollClassName }: {
    series: readonly ReturnsSeries[];
    /** The result's benchmark; a column only while it is the ledger's. */
    benchmark?: ReturnsBenchmark | null;
    /** Whether the range starts at inception, where the first valued row is the first
     *  invested point; a range of dates starts wherever it opens. */
    sinceInception: boolean;
    scrollClassName: string;
}) {
    const bench = drawableBenchmark(benchmark) ? benchmark : null;
    return (
        // Relative, so the screen-reader text in the cells (absolutely placed) is
        // contained by this scroll box. Unpositioned, its containing block is the page's
        // scroll area, where each sits as far down as its row and stretches the page: a
        // second scrollbar beside the table's own.
        <div className={cn('relative', scrollClassName)}>
            <table className="w-full border-collapse text-xs">
                <thead>
                    <tr>
                        <th className={cn(TH, 'text-left')}>Period end</th>
                        <th className={TH}>Index</th>
                        {bench ? (
                            <th
                                className={cn(TH, 'whitespace-nowrap')}
                                title={`${benchmarkLabel(bench.name, bench.symbol)}: total return, set to 100 at this block’s first invested point`}
                            >
                                {bench.symbol} index<span className="sr-only">, benchmark, total return</span>
                            </th>
                        ) : null}
                        <th className={TH}>Period return</th>
                        <th className={TH}>Days covered</th>
                        <th className={TH}>Gain</th>
                        <th className={TH} title="Money-weighted return over the 12 months ending at this period end">
                            Money-weighted, 12 mo
                        </th>
                        <th className={cn(TH, 'w-full text-left')}>Note</th>
                    </tr>
                </thead>
                {series.map((s) => (
                    <SeriesRows
                        key={s.scope === 'ledger' ? 'ledger' : s.accountId}
                        series={s}
                        heading={series.length > 1}
                        benchmark={bench}
                        sinceInception={sinceInception}
                    />
                ))}
            </table>
        </div>
    );
}

function noteFor(
    points: readonly ReturnsSeriesPoint[], i: number, benchmark: ReturnsBenchmark | null, sinceInception: boolean,
): string {
    const p = points[i]!;
    if (p.index === null) {
        // The benchmark pauses with the index, and resumes from its own last value too.
        if (benchmark && p.indexUnavailableReason === 'no_invested_base') {
            return 'No invested balance at this point; the index and the benchmark resume from their last values when money returns.';
        }
        return indexReasonSentence(p);
    }
    const own = (() => {
        const earlier = points.slice(0, i).some((q) => q.index !== null);
        // A range of dates that opens with money invested starts where it opens, not
        // where the money first went in: its first row is only the range's first.
        if (!earlier) return !sinceInception && i === 0 ? '' : 'First invested point.';
        if (points[i - 1]!.index === null) {
            return 'Resumes after a gap; the period return is measured from the index at the gap’s start.';
        }
        return p.periodReturn === null ? reasonSentence(p.periodReturnUnavailableReason) : '';
    })();
    const gap = benchmark ? benchmarkGapSentence(p, benchmark.symbol) : '';
    return [own, gap].filter((s) => s !== '').join(' ');
}

/**
 * A figure the server left null: "no rate" when it needed a missing exchange rate and
 * the server named it, else a dash, with the reason on hover and spoken when `speak`.
 */
function NullCell({ reason, missing, speak }: {
    reason: ReturnsReason | null;
    missing: readonly RateUnavailable[];
    /** Off where the Note column already says why. */
    speak: boolean;
}) {
    if (reason === 'rate_unavailable' && missing.length > 0) {
        return <td className={NIL}><NoRate missing={missing} /></td>;
    }
    const sentence = speak ? reasonSentence(reason) : '';
    return (
        <td className={NIL} title={sentence === '' ? undefined : sentence}>
            —
            {sentence === '' ? null : <span className="sr-only">{sentence}</span>}
        </td>
    );
}

function SeriesRows({ series, heading, benchmark, sinceInception }: {
    series: ReturnsSeries;
    heading: boolean;
    benchmark: ReturnsBenchmark | null;
    sinceInception: boolean;
}) {
    return (
        <tbody>
            {heading ? (
                <tr data-series-heading>
                    <th scope="colgroup" colSpan={benchmark ? 8 : 7} className="border-b border-border bg-surface-muted px-2 py-1 text-left text-[0.6875rem] font-semibold text-text-muted">
                        {seriesName(series)}
                        {series.scope === 'account' ? <span className="ml-2 font-normal text-text-subtle">{series.currencyCode}</span> : null}
                    </th>
                </tr>
            ) : null}
            {series.points.map((p, i) => (
                <tr key={p.periodEnd}>
                    <td className="whitespace-nowrap border-b border-border px-2 py-1 text-left font-mono tabular-nums text-text">
                        {p.periodEnd.slice(0, 10)}
                    </td>
                    {p.index === null
                        ? <NullCell reason={p.indexUnavailableReason} missing={p.rateUnavailable} speak={false} />
                        : <td className={TD}>{formatIndex(p.index)}</td>}
                    {benchmark === null ? null : p.benchmark === null ? (
                        // The Note says why: the index's own reason, or the benchmark's.
                        <NullCell reason={p.benchmarkUnavailableReason} missing={p.benchmarkRateUnavailable} speak={false} />
                    ) : (
                        <td className={TD}>{formatIndex(p.benchmark)}</td>
                    )}
                    <td className={p.periodReturn === null ? NIL : TD}>
                        {p.periodReturn === null ? '—' : formatFraction(p.periodReturn)}
                    </td>
                    <td className={p.index === null ? NIL : TD}>{p.index === null ? '—' : p.coveredDays}</td>
                    {p.gain === null ? (
                        <NullCell reason={p.gainUnavailableReason} missing={p.rateUnavailable} speak />
                    ) : (
                        <td className={TD}>{formatCurrency(p.gain, series.currencyCode)}</td>
                    )}
                    {p.mwrTrailingYear === null ? (
                        <td className="whitespace-nowrap border-b border-border px-2 py-1 text-right text-[0.6875rem] text-text-muted" title={reasonSentence(p.mwrUnavailableReason)}>
                            {p.mwrUnavailableReason === null ? '—' : reasonShort(p.mwrUnavailableReason)}
                        </td>
                    ) : (
                        <td className={TD}>{formatRate(p.mwrTrailingYear)}</td>
                    )}
                    <td className="border-b border-border px-2 py-1 text-left text-[0.6875rem] text-text-muted">
                        {noteFor(series.points, i, benchmark, sinceInception)}
                    </td>
                </tr>
            ))}
        </tbody>
    );
}
