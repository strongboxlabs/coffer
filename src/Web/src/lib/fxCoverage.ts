import { formatLedgerDateCompact } from './dates';
import type { FxPair, RateUnavailable } from './types';

/**
 * How far the rates a "no rate" figure needed have been fetched, as the missing-rate
 * line says it after naming the missing pair and day (ADR-0114 D9, the converted-totals
 * boards CT-3 and CT-4), from the rates view's pairs:
 *
 *   Rates are fetched through Nov 6.
 *   CAD to USD rates are fetched through Nov 6; EUR to USD rates are not fetched yet.
 *
 * One pair is named only when there are several. Null when the view lists none of the
 * missing pairs. Repeats of a pair count once.
 */
export function fetchedThroughText(missing: readonly RateUnavailable[], pairs: readonly FxPair[]): string | null {
    const seen = new Set<string>();
    const listed: FxPair[] = [];
    const ordered = [...missing].sort((a, b) =>
        a.fromCurrency.localeCompare(b.fromCurrency) || a.toCurrency.localeCompare(b.toCurrency));
    for (const m of ordered) {
        const key = `${m.fromCurrency}\u0000${m.toCurrency}`;
        if (seen.has(key)) continue;
        seen.add(key);
        const pair = pairs.find((p) => p.fromCurrency === m.fromCurrency && p.toCurrency === m.toCurrency);
        if (pair) listed.push(pair);
    }
    if (listed.length === 0) return null;
    const through = (p: FxPair) => (p.fetchedThroughDate === null
        ? 'are not fetched yet'
        : `are fetched through ${formatLedgerDateCompact(p.fetchedThroughDate)}`);
    if (listed.length === 1) return `Rates ${through(listed[0]!)}.`;
    return `${listed.map((p) => `${p.fromCurrency} to ${p.toCurrency} rates ${through(p)}`).join('; ')}.`;
}
