// Each currency's ISO 4217 minor units, and which codes an account or a ledger can take
// (ADR-0114 D15).
//
// One checked-in table, `currencyMinorUnits.json` beside this file, holds the exponent of
// every code on ISO 4217 List One, derived from SIX's published list and recording its
// publication date. The API embeds the same file (Coffer.Domain.Money's
// CurrencyMinorUnits), so the SPA and the API read the same places, and both apply the
// one rule below to it.
//
// A code is accepted when the list gives it an exponent of at most MAX_PLACES. Refused: a
// code the list does not hold, an "N.A." entry, and the two fund units with more places
// than the ceiling, CLF and UYW. A stored code that would now be refused keeps working at
// DEFAULT_PLACES, as every code did before, so `minorUnits` answers the default for it,
// never the table's exponent and never `Intl`'s own digits.

import table from './currencyMinorUnits.json';

/** The places a code the table does not accept keeps, as every code had before ADR-0114 D15. */
export const DEFAULT_PLACES = 2;

/** The most places an accepted currency can have (the database's ceiling). */
export const MAX_PLACES = 3;

/** The publication date of the List One the table is derived from (yyyy-MM-dd). */
export const ISO_4217_PUBLISHED: string = table.published;

const LIST_ONE: Readonly<Record<string, number | null>> = table.minorUnits;

function acceptedPlaces(code: string): number | undefined {
    if (!Object.prototype.hasOwnProperty.call(LIST_ONE, code)) return undefined;
    const exponent = LIST_ONE[code];
    return exponent !== null && exponent !== undefined && exponent <= MAX_PLACES ? exponent : undefined;
}

/** Every code an account or a ledger can take, in code order. */
export const ACCEPTED_CURRENCIES: ReadonlyArray<string> = Object.keys(LIST_ONE)
    .filter((code) => acceptedPlaces(code) !== undefined)
    .sort();

/** True when `code` is a currency an account or a ledger can take. */
export function isAcceptedCurrency(code: string): boolean {
    return acceptedPlaces(code) !== undefined;
}

/**
 * The places money in `code` is shown, typed and stored at: the table's exponent for an
 * accepted code, {@link DEFAULT_PLACES} for any other. The same rule as the API's
 * `CurrencyMinorUnits.PlacesOf`.
 */
export function minorUnits(code: string): number {
    return acceptedPlaces(code) ?? DEFAULT_PLACES;
}
