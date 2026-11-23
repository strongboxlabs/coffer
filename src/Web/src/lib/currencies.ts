// The currencies a picker offers (an account's currency, a ledger's reporting currency,
// ADR-0114 D7). One list, so every picker offers the same choices in the same order.
//
// Every code an account or a ledger can take is offered (ADR-0114 D15, ADR-0023 §H):
// the most used first, then every other accepted code in alphabetical order, in the same
// native select. A code the table refuses (CLF, UYW, an N.A. entry, a code not on the
// list) is never offered, but one already stored is still shown (currencyChoices).

import { ACCEPTED_CURRENCIES } from './minorUnits';

/** The most used currencies, which every picker lists first, in this order. */
export const MOST_USED_CURRENCIES: ReadonlyArray<string> = [
    'USD', 'EUR', 'GBP', 'CAD', 'AUD', 'JPY', 'CHF', 'CNY', 'INR', 'MXN', 'BRL', 'SEK', 'NOK', 'NZD',
];

/** The currencies every picker lists: the most used first, then the rest of the accepted codes. */
export const CURRENCIES: ReadonlyArray<string> = [
    ...MOST_USED_CURRENCIES,
    ...ACCEPTED_CURRENCIES
        .filter((code) => !MOST_USED_CURRENCIES.includes(code))
        .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)),
];

/**
 * The choices for a picker whose value is `current`: the list, with `current` first
 * when it is a code the list does not hold (an account, a ledger or an export in a
 * code that is now refused), so the picker can still show what is stored.
 */
export function currencyChoices(current: string): ReadonlyArray<string> {
    return CURRENCIES.includes(current) ? CURRENCIES : [current, ...CURRENCIES];
}
