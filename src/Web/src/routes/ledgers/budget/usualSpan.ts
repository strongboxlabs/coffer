import { formatLedgerDate } from '@/lib/dates';
import type { BudgetProgress } from '@/lib/types';

/**
 * What a month's usual figures average over, for every label that names it.
 *
 * The window is a number of trailing months, but a ledger younger than it has no
 * history before its first transaction, and those months are not averaged
 * (`historyFrom`): each day of the month divides by the window's months in which the
 * ledger already existed on that day. So what the labels can truthfully say is:
 *
 * - **whole months** when the history covers the window, or starts on a month's first
 *   day: one count for every day of the month, said as a number;
 * - **since a day** when it starts mid-month: the days of the month before that day
 *   average one month fewer than the days from it, so no one number is true, and the
 *   label names the day instead;
 * - **none** when it starts on or after the reported month's first day: there is
 *   nothing before the month to average.
 */
export type UsualSpan =
    | { kind: 'months'; months: number }
    | { kind: 'since'; from: string }
    | { kind: 'none' };

export function usualSpan(data: Pick<BudgetProgress, 'month' | 'windowMonths' | 'historyFrom'>): UsualSpan {
    const from = data.historyFrom;
    if (from === null) return { kind: 'months', months: data.windowMonths };
    // Both are yyyy-MM-dd, so the string order is the calendar's.
    if (from >= `${data.month}-01`) return { kind: 'none' };
    if (!from.endsWith('-01')) return { kind: 'since', from };
    const [fromYear, fromMonth] = from.split('-').map(Number);
    const [year, month] = data.month.split('-').map(Number);
    return { kind: 'months', months: (year! - fromYear!) * 12 + (month! - fromMonth!) };
}

/** "month" for one, "3 months" for more: "the month before", "your last 3 months". */
const monthsPhrase = (n: number) => (n === 1 ? 'month' : `${n} months`);

/** The Budget hero's caption for the shaded band, or null when there is no band to explain. */
export function bandLabel(span: UsualSpan): string | null {
    switch (span.kind) {
        case 'months': return `shaded band = your last ${monthsPhrase(span.months)}, same days`;
        case 'since': return `shaded band = your months since ${formatLedgerDate(span.from)}, same days`;
        case 'none': return null;
    }
}

/** The Budget table's caption for what an untargeted row's mark is, in the month named. */
export function unsetLabel(span: UsualSpan, monthName: string): string {
    switch (span.kind) {
        case 'months': return `unset = average of the ${monthsPhrase(span.months)} before ${monthName}`;
        case 'since': return `unset = average of your months since ${formatLedgerDate(span.from)}, before ${monthName}`;
        case 'none': return `unset = no history before ${monthName}`;
    }
}

/** The button that fills the month's targets from each row's usual. */
export function fillLabel(span: UsualSpan): string {
    switch (span.kind) {
        case 'months': return `Fill from ${span.months}-month average`;
        case 'since': return `Fill from average since ${formatLedgerDate(span.from)}`;
        case 'none': return 'Fill from average';
    }
}

/** What an untargeted mark rests on, where a basis line says it. */
export function typicalBasis(span: UsualSpan): string {
    switch (span.kind) {
        case 'months': return `typical, ${span.months} month${span.months === 1 ? '' : 's'}`;
        case 'since': return `typical, since ${formatLedgerDate(span.from)}`;
        case 'none': return 'typical, no history yet';
    }
}
