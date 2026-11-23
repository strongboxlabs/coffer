import { formatLedgerDateLong } from './dates';
import type { RateUnavailable } from './types/conversion';

// Wording for converted figures (ADR-0114 D7, D9). Every figure that combines
// accounts is in the ledger's reporting currency; one that needs a rate the ledger
// does not hold is null, prints as "no rate", and says on hover which rate is missing.

/** The look of the link to the Exchange rates tab each note ends with. */
export const RATES_LINK_CLASS = 'font-medium text-accent hover:underline';

/** What a figure that needs a missing rate prints instead of a number. */
export const NO_RATE_LABEL = 'no rate';

/**
 * What a total that no rule converts prints instead of a number, when the figures it
 * would add are in more than one currency: a category's split paid from accounts in
 * different currencies. No figure adds currencies (ADR-0114 D7). The rail's marker
 * (ADR-0106 D6).
 */
export const MIXED_LABEL = 'mixed';

/**
 * The hover text of {@link MIXED_LABEL}, wherever it stands in for such a total: the
 * split editor's footer and header, the register row, To review (ADR-0114 D7, D14).
 */
export const MIXED_TITLE = 'These lines are paid from accounts in more than one currency, so there is no single total.';

/**
 * The hover text of a figure that needs a missing rate: one sentence per pair, naming
 * its earliest missing day, and how many other days are missing too.
 *
 *   No CAD to USD rate for Nov 10, 2026.
 *   No CAD to USD rate for Nov 8, 2026 and 2 other days. No EUR to USD rate for Nov 9, 2026.
 *
 * Empty for an empty list. Repeats of one pair and day count once.
 */
export function rateUnavailableTitle(missing: readonly RateUnavailable[]): string {
    const daysByPair = new Map<string, { from: string; to: string; days: Set<string> }>();
    for (const m of missing) {
        const key = `${m.fromCurrency}\u0000${m.toCurrency}`;
        const pair = daysByPair.get(key) ?? { from: m.fromCurrency, to: m.toCurrency, days: new Set<string>() };
        pair.days.add(m.date);
        daysByPair.set(key, pair);
    }
    return [...daysByPair.values()]
        .sort((a, b) => a.from.localeCompare(b.from) || a.to.localeCompare(b.to))
        .map(({ from, to, days }) => {
            const sorted = [...days].sort();
            const others = sorted.length - 1;
            const first = formatLedgerDateLong(sorted[0]!);
            const rest = others === 0 ? '' : ` and ${others} other ${others === 1 ? 'day' : 'days'}`;
            return `No ${from} to ${to} rate for ${first}${rest}.`;
        })
        .join(' ');
}

/**
 * The missing stored legs of a derived rate (ADR-0114 D13, D14), one clause per day,
 * earliest first, each leg in the direction it is stored, account currency into the
 * reporting currency:
 *
 *   No A to R rate for <date>
 *   No A or B to R rate for <date>       (both legs missing that day)
 *
 * The rate line, a share move's refusal and Cash ahead's exclusion row all say it this
 * way. Repeats of one leg and day count once; empty for an empty list.
 */
export function missingLegsByDay(missing: readonly RateUnavailable[]): string[] {
    const byDay = new Map<string, { to: string; date: string; legs: string[] }>();
    for (const m of missing) {
        const key = `${m.date}\u0000${m.toCurrency}`;
        const day = byDay.get(key) ?? { to: m.toCurrency, date: m.date, legs: [] };
        if (!day.legs.includes(m.fromCurrency)) day.legs.push(m.fromCurrency);
        byDay.set(key, day);
    }
    return [...byDay.values()]
        .sort((a, b) => a.date.localeCompare(b.date) || a.to.localeCompare(b.to))
        .map(({ to, date, legs }) => `No ${legs.join(' or ')} to ${to} rate for ${formatLedgerDateLong(date)}`);
}

/** {@link missingLegsByDay} as sentences, for a refusal that names them. */
export function missingLegsSentences(missing: readonly RateUnavailable[]): string {
    return missingLegsByDay(missing).map((clause) => `${clause}.`).join(' ');
}

/**
 * The ledger-level conversion note's sentence, or null when nothing converts: no
 * account is in a currency other than the reporting one.
 *
 *   Totals are in USD, with CAD accounts converted at the rate for each figure's date
 *   (the latest rate from today on). An account's own figures stay in its currency.
 *
 * The rate is the date rule of ADR-0114 D7: a figure for a day before today converts at
 * that day's rate, a figure for today or later (a current total, a forecast) at the
 * latest one. "Each day's rate" would be wrong for every current total.
 *
 * `currencies` are the ledger's accounts' currencies, in any order, repeats and the
 * reporting currency included.
 */
export function conversionNoteText(reportingCurrency: string, currencies: Iterable<string>): string | null {
    const others = [...new Set(currencies)].filter((c) => c !== reportingCurrency).sort();
    if (others.length === 0) return null;
    const list = others.length === 1
        ? others[0]!
        : `${others.slice(0, -1).join(', ')} and ${others[others.length - 1]!}`;
    return `Totals are in ${reportingCurrency}, with ${list} accounts converted at the rate for each figure's date `
        + '(the latest rate from today on). '
        + 'An account\'s own figures stay in its currency.';
}
