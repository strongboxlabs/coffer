import { describe, expect, it } from 'vitest';

import { fetchedThroughText } from './fxCoverage';
import type { FxPair, RateUnavailable } from './types';

// The fetched-through sentence the missing-rate line ends with (ADR-0114 D9). Every
// pair and day is invented.

const missing = (fromCurrency: string, date: string): RateUnavailable =>
    ({ fromCurrency, toCurrency: 'USD', date, reason: 'rate_unavailable' });

const pair = (fromCurrency: string, fetchedThroughDate: string | null): FxPair => ({
    fromCurrency, toCurrency: 'USD', neededFor: ['net-worth'], neededFrom: '2026-01-01',
    fetchedFromDate: fetchedThroughDate === null ? null : '2026-01-01', fetchedThroughDate,
    latestManualDate: null, coveredThroughDate: fetchedThroughDate, rateCount: 0,
    firstRateDate: null, missingDays: [], neededBy: [],
});

describe('fetchedThroughText', () => {
    it('says how far one pair is fetched, once however many of its days are missing', () => {
        expect(fetchedThroughText([missing('CAD', '2026-11-08'), missing('CAD', '2026-11-09')], [pair('CAD', '2026-11-06')]))
            .toBe('Rates are fetched through Nov 6.');
    });

    it('says a pair has not been fetched at all', () => {
        expect(fetchedThroughText([missing('CAD', '2026-11-08')], [pair('CAD', null)])).toBe('Rates are not fetched yet.');
    });

    it('names each pair when several are missing, in order', () => {
        expect(fetchedThroughText(
            [missing('EUR', '2026-11-08'), missing('CAD', '2026-11-08')],
            [pair('EUR', null), pair('CAD', '2026-11-06')],
        )).toBe('CAD to USD rates are fetched through Nov 6; EUR to USD rates are not fetched yet.');
    });

    it('says nothing of a pair the view does not list, and nothing at all when it lists none', () => {
        expect(fetchedThroughText([missing('CAD', '2026-11-08'), missing('GBP', '2026-11-08')], [pair('CAD', '2026-11-06')]))
            .toBe('Rates are fetched through Nov 6.');
        expect(fetchedThroughText([missing('GBP', '2026-11-08')], [pair('CAD', '2026-11-06')])).toBeNull();
        expect(fetchedThroughText([], [pair('CAD', '2026-11-06')])).toBeNull();
    });
});
