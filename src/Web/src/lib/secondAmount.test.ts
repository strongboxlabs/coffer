import { describe, expect, it } from 'vitest';

import {
    bankDirection,
    rateLineText,
    secondAmountDisplay,
    secondAmountMagnitude,
    signedSecondAmount,
} from './secondAmount';
import type { DerivedRate } from './types/marketSeries';

// The words and the sign of a transfer's second amount (ADR-0114 D13, D14).

const answer = (over: Partial<DerivedRate>): DerivedRate => ({
    fromCurrency: 'USD', toCurrency: 'EUR', date: '2026-11-12', reportingCurrency: 'GBP',
    rate: null, rateDate: null, legs: [], converted: null, rateUnavailable: [], ...over,
});

describe('rateLineText', () => {
    it('names the derived rate and the older stored leg\'s day', () => {
        expect(rateLineText({ kind: 'derived', answer: answer({ rate: 0.9, rateDate: '2026-11-10' }) }))
            .toBe('Rate for Nov 10, 2026: 1 USD = 0.9000 EUR');
    });

    it('names the one missing stored leg into the reporting currency', () => {
        expect(rateLineText({
            kind: 'derived',
            answer: answer({ rateUnavailable: [{ fromCurrency: 'EUR', toCurrency: 'GBP', date: '2026-11-12', reason: 'rate_unavailable' }] }),
        })).toBe('No EUR to GBP rate for Nov 12, 2026');
    });

    it('names both stored legs when both are missing', () => {
        expect(rateLineText({
            kind: 'derived',
            answer: answer({
                rateUnavailable: [
                    { fromCurrency: 'USD', toCurrency: 'GBP', date: '2026-11-12', reason: 'rate_unavailable' },
                    { fromCurrency: 'EUR', toCurrency: 'GBP', date: '2026-11-12', reason: 'rate_unavailable' },
                ],
            }),
        })).toBe('No USD or EUR to GBP rate for Nov 12, 2026');
    });

    it('shows the ratio of two amounts, this side\'s currency first', () => {
        expect(rateLineText({ kind: 'ratio', from: 'USD', to: 'EUR', amount: -100, other: 90.5 }))
            .toBe('1 USD = 0.9050 EUR, from the two amounts');
        expect(rateLineText({ kind: 'ratio', from: 'USD', to: 'EUR', amount: 0, other: 90.5 })).toBe('');
    });
});

describe('the sign', () => {
    it('reads money leaving the source as received by the other account', () => {
        expect(bankDirection('-100.00')).toBe('received');
        expect(bankDirection('100.00')).toBe('sent');
        expect(bankDirection('')).toBe('received');
    });

    it('drops a typed minus and shows the sent figure negative', () => {
        expect(secondAmountMagnitude('-95.00')).toBe('95.00');
        expect(secondAmountDisplay('95.00', 'sent')).toBe('-95.00');
        expect(secondAmountDisplay('95.00', 'received')).toBe('95.00');
        expect(signedSecondAmount(95, 'sent')).toBe(-95);
        expect(signedSecondAmount(-95, 'received')).toBe(95);
    });
});
