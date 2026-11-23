import { describe, it, expect } from 'vitest';

import table from './currencyMinorUnits.json';
import {
    ACCEPTED_CURRENCIES,
    DEFAULT_PLACES,
    ISO_4217_PUBLISHED,
    MAX_PLACES,
    isAcceptedCurrency,
    minorUnits,
} from './minorUnits';

// The rule (ADR-0114 D15), applied here from the raw file code by code, as the API's
// CurrencyMinorUnitsTests applies it to PlacesOf and the SQL guard to the SQL function:
// a code the list gives an exponent of at most three takes it, every other code takes two.

const LIST_ONE: Readonly<Record<string, number | null>> = table.minorUnits;
const CODES = Object.keys(LIST_ONE);

const exponentOf = (code: string): number | null | undefined => LIST_ONE[code];
const expectedAccepted = (code: string): boolean => {
    const exponent = exponentOf(code);
    return exponent !== null && exponent !== undefined && exponent <= 3;
};
const expectedPlaces = (code: string): number => (expectedAccepted(code) ? (exponentOf(code) as number) : 2);

// The 0-place and 3-place sets, pinned so a stale or mistyped table fails here.
const WHOLE_UNIT_CODES = [
    'BIF', 'CLP', 'DJF', 'GNF', 'ISK', 'JPY', 'KMF', 'KRW', 'PYG', 'RWF', 'UGX', 'UYI',
    'VND', 'VUV', 'XAF', 'XOF', 'XPF',
];
const THREE_PLACE_CODES = ['BHD', 'IQD', 'JOD', 'KWD', 'LYD', 'OMR', 'TND'];

describe('minorUnits', () => {
    it('reads the whole list, with its publication date', () => {
        // Fail closed: a list that read as nearly empty would pass every per-code check.
        expect(CODES.length).toBeGreaterThan(150);
        expect(ISO_4217_PUBLISHED).toMatch(/^\d{4}-\d{2}-\d{2}$/);
        expect(DEFAULT_PLACES).toBe(2);
        expect(MAX_PLACES).toBe(3);
    });

    it('gives every listed code its places when accepted and two otherwise', () => {
        for (const code of CODES) {
            expect([code, minorUnits(code)]).toEqual([code, expectedPlaces(code)]);
            expect([code, isAcceptedCurrency(code)]).toEqual([code, expectedAccepted(code)]);
        }
    });

    it('answers only whole units, two places or three', () => {
        for (const code of CODES) expect([0, 2, 3]).toContain(minorUnits(code));
    });

    it('names the whole-unit and three-place currencies the list names', () => {
        expect(minorUnits('JPY')).toBe(0);
        expect(minorUnits('USD')).toBe(2);
        expect(minorUnits('KWD')).toBe(3);
        expect(ACCEPTED_CURRENCIES.filter((c) => minorUnits(c) === 0)).toEqual(WHOLE_UNIT_CODES);
        expect(ACCEPTED_CURRENCIES.filter((c) => minorUnits(c) === 3)).toEqual(THREE_PLACE_CODES);
    });

    it.each([
        // The two fund units the list gives four places.
        'CLF',
        'UYW',
        // "N.A." entries: a metal and the no-currency code.
        'XAU',
        'XXX',
        // Not on the list at all, and not in the stored form.
        'ABC',
        'jpy',
    ])('refuses %s and keeps it at two places', (code) => {
        expect(isAcceptedCurrency(code)).toBe(false);
        expect(ACCEPTED_CURRENCIES).not.toContain(code);
        expect(minorUnits(code)).toBe(2);
    });

    it('holds the fund units at four places and an N.A. entry as none, so the refusal is the rule', () => {
        expect(LIST_ONE.CLF).toBe(4);
        expect(LIST_ONE.UYW).toBe(4);
        expect(LIST_ONE.XAU).toBeNull();
        expect(Object.prototype.hasOwnProperty.call(LIST_ONE, 'ABC')).toBe(false);
    });

    it('lists every accepted code once, in code order', () => {
        const accepted = CODES.filter(expectedAccepted).sort();
        expect(ACCEPTED_CURRENCIES).toEqual(accepted);
    });
});
