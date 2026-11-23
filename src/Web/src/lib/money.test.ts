import { describe, it, expect } from 'vitest';

import table from './currencyMinorUnits.json';
import {
    amountInputMode,
    amountPlaceholder,
    amountStep,
    amountText,
    atDisplayPrecision,
    currencyAffix,
    formatAsSent,
    formatCurrency,
    formatCurrencyWhole,
    formatFxRate,
    formatPrice,
    formatSignedAmount,
    roundToMinorUnits,
} from './money';

// Each currency's places come from the ISO 4217 table, never from Intl's own digits
// (ADR-0114 D15). A reference formatter with the digits stated is what the canonical
// formatters must print; the expected places are worked out here from the raw file.

const LIST_ONE: Readonly<Record<string, number | null | undefined>> = table.minorUnits;
const CODES = Object.keys(LIST_ONE);

function isoPlaces(code: string): number {
    const exponent = LIST_ONE[code];
    return exponent !== null && exponent !== undefined && exponent <= 3 ? exponent : 2;
}

function reference(code: string, min: number, max: number, signDisplay?: 'auto'): Intl.NumberFormat {
    return new Intl.NumberFormat(undefined, {
        style: 'currency',
        currency: code,
        minimumFractionDigits: min,
        maximumFractionDigits: max,
        ...(signDisplay ? { signDisplay } : {}),
    });
}

// A whole figure shows the minimum places (trailing zeros); a long fraction shows the
// maximum. A negative carries the sign.
const PROBES = [1, 1234.56789, -0.123456];

describe('formatCurrency and formatSignedAmount', () => {
    it('print every listed currency at its own places', () => {
        expect(CODES.length).toBeGreaterThan(150);
        for (const code of CODES) {
            const places = isoPlaces(code);
            for (const value of PROBES) {
                expect([code, value, formatCurrency(value, code)])
                    .toEqual([code, value, reference(code, places, places).format(value)]);
                expect([code, value, formatSignedAmount(value, code)])
                    .toEqual([code, value, reference(code, places, places, 'auto').format(value)]);
            }
        }
    });

    it('follow ISO 4217 where the browser\'s own digits differ from it', () => {
        const differing = CODES.filter((code) => {
            const own = new Intl.NumberFormat(undefined, { style: 'currency', currency: code }).resolvedOptions();
            return own.maximumFractionDigits !== isoPlaces(code);
        });
        // Fail loudly rather than pass with nothing checked: the ISO and CLDR digits are
        // known to differ for several codes, so an empty list means this test is blind.
        expect(differing.length).toBeGreaterThan(0);
        for (const code of differing) {
            const places = isoPlaces(code);
            expect([code, formatCurrency(1.5, code)]).toEqual([code, reference(code, places, places).format(1.5)]);
        }
    });

    it('print a stored code the table refuses at two places, not the table\'s or the browser\'s', () => {
        // CLF: the list gives it four places, and so does the browser.
        expect(formatCurrency(1.23456, 'CLF')).toBe(reference('CLF', 2, 2).format(1.23456));
        expect(formatCurrency(1.23456, 'CLF')).not.toBe(reference('CLF', 4, 4).format(1.23456));
        // A code the list does not hold.
        expect(formatCurrency(1.23456, 'ABC')).toBe(reference('ABC', 2, 2).format(1.23456));
        // An N.A. entry.
        expect(formatSignedAmount(-1.23456, 'XAU')).toBe(reference('XAU', 2, 2, 'auto').format(-1.23456));
    });
});

describe('formatPrice', () => {
    it('shows at least the currency\'s places and at most six, or the currency\'s if more', () => {
        // Three-place currency: three to six.
        expect(formatPrice(1, 'KWD')).toBe(reference('KWD', 3, 3).format(1));
        expect(formatPrice(1.123456789, 'KWD')).toBe(reference('KWD', 6, 6).format(1.123456789));
        // Whole-unit currency: none to six.
        expect(formatPrice(5, 'JPY')).toBe(reference('JPY', 0, 0).format(5));
        expect(formatPrice(1.123456789, 'JPY')).toBe(reference('JPY', 6, 6).format(1.123456789));
        // Two-place currency: two to six, as before.
        expect(formatPrice(1, 'USD')).toBe(reference('USD', 2, 2).format(1));
        expect(formatPrice(1.123456789, 'USD')).toBe(reference('USD', 6, 6).format(1.123456789));
    });
});

describe('atDisplayPrecision', () => {
    it('rounds a whole-unit currency to whole units, a half away from zero', () => {
        expect(atDisplayPrecision(0.5, 'JPY')).toBe(1);
        expect(atDisplayPrecision(-0.5, 'JPY')).toBe(-1);
        expect(atDisplayPrecision(2.4, 'JPY')).toBe(2);
    });

    it('takes a residue below a whole unit to a plain zero', () => {
        expect(Object.is(atDisplayPrecision(-0.3, 'JPY'), 0)).toBe(true);
    });

    it('keeps a three-place currency\'s third place', () => {
        expect(atDisplayPrecision(1.2346, 'KWD')).toBe(1.235);
    });
});

describe('formatFxRate', () => {
    it('prints one unit of the first currency in the second, at four to six places', () => {
        expect(formatFxRate(0.72, 'CAD', 'USD')).toBe('1 CAD = 0.7200 USD');
        expect(formatFxRate(0.0066123456, 'JPY', 'USD')).toBe('1 JPY = 0.006612 USD');
    });

    it('prints nothing for a rate it is not given', () => {
        expect(formatFxRate(null, 'CAD', 'USD')).toBe('');
    });
});

describe('formatCurrencyWhole', () => {
    it('prints whole units with no cents, at seven digits too', () => {
        expect(formatCurrencyWhole(24269.4, 'USD')).toBe('$24,269');
        expect(formatCurrencyWhole(1234567, 'USD')).toBe('$1,234,567');
    });
});

// The one money rounding (ADR-0114 D15): half away from zero, at the currency's places,
// and nothing at all while the currency is null.
describe('roundToMinorUnits', () => {
    it('rounds a whole-unit currency half away from zero, of either sign', () => {
        expect(roundToMinorUnits(2.5, 'JPY')).toBe(3);
        // A negative midpoint goes down, away from zero: a Math.round rounding would go up.
        expect(roundToMinorUnits(-2.5, 'JPY')).toBe(-3);
        expect(roundToMinorUnits(2.4, 'JPY')).toBe(2);
        expect(roundToMinorUnits(-2.4, 'JPY')).toBe(-2);
    });

    it('rounds a two-place midpoint the double holds just under it, away from zero', () => {
        // The double nearest this midpoint is below it, so rounding the binary value
        // (as a fixed-digits rounding does) goes down; the decimal digits go up.
        expect(roundToMinorUnits(1.005, 'USD')).toBe(1.01);
        expect(roundToMinorUnits(-1.005, 'USD')).toBe(-1.01);
        expect(roundToMinorUnits(0.125, 'EUR')).toBe(0.13);
        expect(roundToMinorUnits(1.004, 'USD')).toBe(1);
    });

    it('rounds a three-place currency at its third place', () => {
        expect(roundToMinorUnits(1.0005, 'KWD')).toBe(1.001);
        expect(roundToMinorUnits(-1.0005, 'KWD')).toBe(-1.001);
        expect(roundToMinorUnits(1.2344, 'KWD')).toBe(1.234);
    });

    it('returns a figure already at its places as it is, and a residue as a plain zero', () => {
        expect(roundToMinorUnits(12.34, 'USD')).toBe(12.34);
        expect(roundToMinorUnits(7, 'JPY')).toBe(7);
        expect(Object.is(roundToMinorUnits(-0.0000001, 'USD'), 0)).toBe(true);
        expect(roundToMinorUnits(0.0000006, 'KWD')).toBe(0);
    });

    it('rounds a stored code the table refuses at two places, as it is shown', () => {
        expect(roundToMinorUnits(1.235, 'CLF')).toBe(1.24);
    });

    it('rounds nothing while the currency is null', () => {
        expect(roundToMinorUnits(1.23456, null)).toBe(1.23456);
        expect(roundToMinorUnits(-2.5, undefined)).toBe(-2.5);
    });
});

describe('the money input helpers', () => {
    it('give a whole-unit currency a whole step, a bare zero and the numeric keypad', () => {
        expect(amountStep('JPY')).toBe('1');
        expect(amountPlaceholder('JPY')).toBe('0');
        expect(amountInputMode('JPY')).toBe('numeric');
    });

    it('give a three-place currency a step and a placeholder at three places', () => {
        expect(amountStep('KWD')).toBe('0.001');
        expect(amountPlaceholder('KWD')).toBe('0.000');
        expect(amountInputMode('KWD')).toBe('decimal');
    });

    it('give a two-place currency the step and placeholder it always had', () => {
        expect(amountStep('USD')).toBe('0.01');
        expect(amountPlaceholder('USD')).toBe('0.00');
        expect(amountInputMode('USD')).toBe('decimal');
    });

    it('default nothing while the currency is null', () => {
        expect(amountStep(null)).toBe('any');
        expect(amountPlaceholder(null)).toBe('');
        expect(amountInputMode(null)).toBe('decimal');
    });
});

describe('amountText', () => {
    it('seeds a stored figure with its trailing zeros, at the currency\'s places', () => {
        expect(amountText(1.5, 'KWD')).toBe('1.500');
        expect(amountText(5, 'USD')).toBe('5.00');
        expect(amountText(-0.1, 'USD')).toBe('-0.10');
        expect(amountText(12, 'JPY')).toBe('12');
    });

    it('keeps every digit of a stored figure finer than its currency', () => {
        // Stored before each currency kept its own places (ADR-0114 D15): seeding must not
        // round it, or an edit that leaves it alone would change it.
        expect(amountText(1234.5, 'JPY')).toBe('1234.5');
        expect(amountText(-1.2345, 'USD')).toBe('-1.2345');
    });

    it('writes a figure out without an exponent', () => {
        expect(amountText(0.0000001, 'USD')).toBe('0.0000001');
    });

    it('seeds no figure as empty, and pads nothing while the currency is null', () => {
        expect(amountText(null, 'USD')).toBe('');
        expect(amountText(undefined, 'USD')).toBe('');
        expect(amountText(1.5, null)).toBe('1.5');
        expect(amountText(-0, 'USD')).toBe('0.00');
    });
});

describe('currencyAffix', () => {
    it('puts the symbol before the figure where the formatter does', () => {
        expect(currencyAffix('USD', 'en-US')).toEqual({ symbol: '$', position: 'before' });
        // A dollar in another currency is told apart, as the displayed amount does.
        expect(currencyAffix('CAD', 'en-US')).toEqual({ symbol: 'CA$', position: 'before' });
    });

    it('puts the symbol after the figure for a locale that writes it there', () => {
        expect(currencyAffix('EUR', 'de-DE')).toEqual({ symbol: '€', position: 'after' });
    });

    it('agrees with formatCurrency in the browser\'s own locale', () => {
        const affix = currencyAffix('EUR');
        expect(affix).not.toBeNull();
        const printed = formatCurrency(1, 'EUR');
        const at = printed.indexOf(affix!.symbol);
        expect(at).toBeGreaterThanOrEqual(0);
        expect(affix!.position).toBe(at < printed.indexOf('1') ? 'before' : 'after');
    });

    it('gives no symbol for a null currency or a code still being typed', () => {
        expect(currencyAffix(null)).toBeNull();
        expect(currencyAffix('')).toBeNull();
        expect(currencyAffix('US')).toBeNull();
    });
});

describe('formatAsSent', () => {
    it('keeps every digit, a trailing zero and the sign, grouped for the locale', () => {
        expect(formatAsSent('1234.5', 'en-US')).toBe('1,234.5');
        expect(formatAsSent('-1234.50', 'en-US')).toBe('-1,234.50');
        expect(formatAsSent('1234567.000', 'en-US')).toBe('1,234,567.000');
        expect(formatAsSent('12', 'en-US')).toBe('12');
    });

    it('groups and points for a locale that writes a decimal comma', () => {
        expect(formatAsSent('-1234.50', 'de-DE')).toBe('-1.234,50');
    });

    it('writes every digit, the fraction too, in a locale whose numerals are not Latin', () => {
        const shown = formatAsSent('-1234.50', 'ar-EG');
        expect(shown).not.toMatch(/[0-9]/);
        expect(shown).toMatch(/٫[٠-٩]{2}$/);
        expect(shown).toBe(
            new Intl.NumberFormat('ar-EG', { minimumFractionDigits: 2 }).format(-1234.5),
        );
    });

    it('keeps every digit of a figure longer than a double holds, and a sent plus sign', () => {
        expect(formatAsSent('12345678901234567890.123456789', 'en-US')).toBe(
            '12,345,678,901,234,567,890.123456789',
        );
        expect(formatAsSent('+3.0', 'en-US')).toBe('+3.0');
    });

    it('returns text that is not a plain decimal as it came', () => {
        expect(formatAsSent('n/a', 'en-US')).toBe('n/a');
    });
});
