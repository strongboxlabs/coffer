import { describe, expect, it } from 'vitest';

import { estimateTitle, isEstimating } from './reminderEstimate';
import type { ReminderEstimate } from './types/reminder';

const made: ReminderEstimate = { requestedSampleCount: 3, availableSampleCount: 2, amount: -50, unavailableReason: null };
const notYet: ReminderEstimate = { requestedSampleCount: 3, availableSampleCount: 0, amount: null, unavailableReason: 'no-history' };

describe('isEstimating', () => {
    it('is true for a series that estimates, with an estimate made or not', () => {
        expect(isEstimating({ estimate: made })).toBe(true);
        expect(isEstimating({ estimate: notYet })).toBe(true);
    });

    it('is false for a fixed-amount series and a posted occurrence', () => {
        expect(isEstimating({})).toBe(false);
        expect(isEstimating({ estimate: null })).toBe(false);
        expect(isEstimating(undefined)).toBe(false);
    });
});

describe('estimateTitle', () => {
    it('names how many occurrences an estimate averages', () => {
        expect(estimateTitle(made)).toBe('Estimated from the last 2 occurrences');
        expect(estimateTitle({ ...made, availableSampleCount: 1 })).toBe('Estimated from the last 1 occurrence');
    });

    it('says the amount entered stands in while no estimate can be made', () => {
        expect(estimateTitle(notYet)).toBe('Estimated: the amount entered is used until an estimate can be made');
    });
});
