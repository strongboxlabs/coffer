import { describe, expect, it } from 'vitest';

import { CURRENCIES, MOST_USED_CURRENCIES, currencyChoices } from './currencies';
import table from './currencyMinorUnits.json';

// The account and ledger currency pickers offer every code an account or a ledger can
// take, the most used first and the rest alphabetically (ADR-0114 D15). What is
// accepted is worked out here from the raw table, by the rule, not from minorUnits.ts.

const LIST_ONE: Readonly<Record<string, number | null>> = table.minorUnits;
const isAccepted = (code: string): boolean => {
    const exponent = LIST_ONE[code];
    return exponent !== null && exponent !== undefined && exponent <= 3;
};
const ACCEPTED = Object.keys(LIST_ONE).filter(isAccepted);

describe('CURRENCIES', () => {
    it('lists the most used currencies first, in their order', () => {
        expect(MOST_USED_CURRENCIES).toEqual([
            'USD', 'EUR', 'GBP', 'CAD', 'AUD', 'JPY', 'CHF', 'CNY', 'INR', 'MXN', 'BRL', 'SEK', 'NOK', 'NZD',
        ]);
        expect(CURRENCIES.slice(0, MOST_USED_CURRENCIES.length)).toEqual(MOST_USED_CURRENCIES);
    });

    it('lists the rest in alphabetical order', () => {
        const rest = CURRENCIES.slice(MOST_USED_CURRENCIES.length);
        // Fail closed: an empty rest would be trivially sorted.
        expect(rest.length).toBeGreaterThan(100);
        expect(rest).toEqual([...rest].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)));
    });

    it('offers every accepted code exactly once, and nothing else', () => {
        expect(new Set(CURRENCIES).size).toBe(CURRENCIES.length);
        expect([...CURRENCIES].sort()).toEqual([...ACCEPTED].sort());
        // The refused codes: the two fund units with four places, and every N.A. entry.
        const refused = Object.keys(LIST_ONE).filter((code) => !isAccepted(code));
        expect(refused).toEqual(expect.arrayContaining(['CLF', 'UYW']));
        for (const code of refused) expect(CURRENCIES).not.toContain(code);
    });

    it('offers whole-unit and three-place currencies', () => {
        const whole = ACCEPTED.filter((code) => LIST_ONE[code] === 0);
        const three = ACCEPTED.filter((code) => LIST_ONE[code] === 3);
        expect(whole.length).toBeGreaterThan(0);
        expect(three.length).toBeGreaterThan(0);
        expect(CURRENCIES).toEqual(expect.arrayContaining([...whole, ...three]));
    });
});

describe('currencyChoices', () => {
    it('offers the list as it is for a code it holds', () => {
        expect(currencyChoices('KWD')).toBe(CURRENCIES);
    });

    it('still shows a stored code that is now refused, first', () => {
        for (const stored of ['CLF', 'XAU', 'ABC']) {
            const choices = currencyChoices(stored);
            expect(choices[0]).toBe(stored);
            expect(choices.slice(1)).toEqual(CURRENCIES);
        }
    });
});
