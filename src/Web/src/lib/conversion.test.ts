import { describe, expect, it } from 'vitest';

import { conversionNoteText, rateUnavailableTitle } from './conversion';
import type { RateUnavailable } from './types/conversion';

const miss = (fromCurrency: string, toCurrency: string, date: string): RateUnavailable =>
    ({ fromCurrency, toCurrency, date, reason: 'rate_unavailable' });

describe('rateUnavailableTitle', () => {
    it('names the pair and the day', () => {
        expect(rateUnavailableTitle([miss('CAD', 'USD', '2026-11-10')])).toBe('No CAD to USD rate for Nov 10, 2026.');
    });

    it('gives each pair one sentence, its earliest day and how many other days are missing', () => {
        expect(rateUnavailableTitle([
            miss('EUR', 'USD', '2026-01-30'),
            miss('CAD', 'USD', '2026-02-02'),
            miss('CAD', 'USD', '2026-01-31'),
            miss('CAD', 'USD', '2026-02-01'),
        ])).toBe('No CAD to USD rate for Jan 31, 2026 and 2 other days. No EUR to USD rate for Jan 30, 2026.');
    });

    it('counts a repeated pair and day once', () => {
        expect(rateUnavailableTitle([miss('CAD', 'USD', '2026-11-10'), miss('CAD', 'USD', '2026-11-10'), miss('CAD', 'USD', '2026-11-11')]))
            .toBe('No CAD to USD rate for Nov 10, 2026 and 1 other day.');
    });

    it('is empty when nothing is missing', () => {
        expect(rateUnavailableTitle([])).toBe('');
    });
});

describe('conversionNoteText', () => {
    it('says nothing when every account is in the reporting currency', () => {
        expect(conversionNoteText('USD', ['USD', 'USD'])).toBeNull();
        expect(conversionNoteText('USD', [])).toBeNull();
    });

    it('names the currencies that convert, once each and in order', () => {
        expect(conversionNoteText('USD', ['USD', 'CAD', 'CAD']))
            .toBe('Totals are in USD, with CAD accounts converted at the rate for each figure\'s date (the latest rate from today on). An account\'s own figures stay in its currency.');
        expect(conversionNoteText('USD', ['GBP', 'CAD', 'USD', 'EUR']))
            .toBe('Totals are in USD, with CAD, EUR and GBP accounts converted at the rate for each figure\'s date (the latest rate from today on). An account\'s own figures stay in its currency.');
    });
});
