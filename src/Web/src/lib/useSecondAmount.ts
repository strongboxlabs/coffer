// The prefill and the rate line of a transfer's second amount (ADR-0114 D14), shared by
// the bank editor's one line, each split line and the investment editor.

import { useEffect } from 'react';

import { useDerivedRate } from './marketSeriesQueries';
import { amountText } from './money';
import { rateLineText } from './secondAmount';

export interface SecondAmountArgs {
    ledgerId: string;
    /** Amount's currency, the source side's. */
    fromCurrency: string;
    /** The second amount's currency, the other account's. */
    toCurrency: string;
    /** The entry's date, `yyyy-MM-dd`; blank while it has none. */
    date: string;
    /** Amount's figure, signed or not; null while it is blank or unparseable. */
    amount: number | null;
    /** The second amount's magnitude text. */
    value: string;
    /** True once the field is typed in, or seeded from a stored or copied figure: it is
     *  never refilled then (ADR-0114 D14, a saved transaction is never refilled). */
    fixed: boolean;
    /** Writes the prefilled magnitude, '' while there is none for the live Amount. */
    onPrefill: (magnitude: string) => void;
}

/**
 * While the field follows (a new entry, or a posting that gains the field in the editor),
 * it holds Amount at the derived rate for the date, rounded once by the server at the
 * other currency's places, and follows Amount and the date. Nothing is asked once it is
 * fixed. The prefill is written into the draft, never shown from the answer alone, so a
 * save sends exactly what the field shows: while the answer for the live Amount is not
 * in (the debounce, the request), the field is empty and required.
 *
 * Returns the rate line.
 */
export function useSecondAmount({
    ledgerId, fromCurrency, toCurrency, date, amount, value, fixed, onPrefill,
}: SecondAmountArgs): string {
    // The magnitude: the sign follows from Amount, so flipping it asks nothing new. A zero
    // or blank Amount asks for the rate alone, and the field stays empty.
    const magnitude = amount === null || amount === 0 ? null : Math.abs(amount);
    const derived = useDerivedRate({
        ledgerId,
        fromCurrency,
        toCurrency,
        date: date.length > 0 ? date : null,
        amount: magnitude,
        enabled: !fixed,
    });
    const answer = fixed ? undefined : derived.data;
    const prefill = answer?.converted === null || answer?.converted === undefined
        ? ''
        : amountText(Math.abs(answer.converted), toCurrency);

    // Writes only a change, so a caller's new writer on each render costs nothing.
    useEffect(() => {
        if (!fixed && prefill !== value) onPrefill(prefill);
    }, [fixed, prefill, value, onPrefill]);

    if (fixed) {
        const other = value.trim().length === 0 ? null : Number(value);
        return rateLineText({
            kind: 'ratio',
            from: fromCurrency,
            to: toCurrency,
            amount,
            other: other !== null && Number.isFinite(other) ? other : null,
        });
    }
    return rateLineText({ kind: 'derived', answer });
}
