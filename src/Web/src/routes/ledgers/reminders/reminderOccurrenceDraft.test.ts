import { describe, expect, it } from 'vitest';

import type { ReminderDetail, ReminderLegDto } from '@/lib/types';

import { reminderBankPrefill, reminderInvestmentDraft } from './reminderOccurrenceDraft';

// The one web prefill behind Post or skip… and the series editor (ADR-0114 D14): it
// carries the counterpart's own amount as the posting's second amount, so a transfer
// between currencies opens with both of its amounts.

const leg = (over: Partial<ReminderLegDto>): ReminderLegDto => ({
    legId: 'l', accountId: 'a', accountName: 'Account', postingIndex: 0, amount: 0, legMemo: null, legTags: [],
    securityId: null, securityTicker: null, quantity: null, unitPrice: null, postingRole: null, ...over,
});

const detail = (over: Partial<ReminderDetail>): ReminderDetail => ({
    id: 'r1', kind: 'bank', payee: 'Payee A', memo: null, tags: [], checkNumber: null, action: null,
    rrule: 'FREQ=MONTHLY;BYMONTHDAY=1', startDate: '2026-01-01', endDate: null, nextDueDate: '2026-11-01',
    autoCommitDaysBefore: null, isActive: true, isLoanReminder: false, origin: 'manual',
    sourceAccountId: 'usd-bank', legs: [], ...over,
});

const transfer = detail({
    legs: [
        leg({ legId: 'src', accountId: 'usd-bank', amount: -100 }),
        leg({ legId: 'other', accountId: 'eur-bank', amount: 90 }),
    ],
});

describe('reminderBankPrefill', () => {
    it('carries the counterpart leg\'s own amount as the posting\'s second amount', () => {
        const posting = reminderBankPrefill(transfer)!.prefill.postings![0]!;
        expect(posting).toMatchObject({ counterpartyAccountId: 'eur-bank', amount: -100, counterpartyAmount: 90 });
    });

    it('seeds both sides of an occurrence\'s estimate when it has the other side\'s', () => {
        const posting = reminderBankPrefill(transfer, {
            requestedSampleCount: 3, availableSampleCount: 3, amount: -120, unavailableReason: null,
            counterpartyAmount: 108,
        })!.prefill.postings![0]!;
        expect(posting).toMatchObject({ amount: -120, counterpartyAmount: 108 });
    });

    it('leaves the second amount to be entered when the due date has no rate', () => {
        const posting = reminderBankPrefill(transfer, {
            requestedSampleCount: 3, availableSampleCount: 3, amount: null, unavailableReason: 'rate-unavailable',
        })!.prefill.postings![0]!;
        expect(posting).toMatchObject({ amount: -100, counterpartyAmount: null });
    });

    it('opens a same-currency occurrence on its estimate, as the subtitle and auto-post read it', () => {
        const sameCurrency = detail({
            legs: [
                leg({ legId: 'src', accountId: 'usd-bank', amount: -100 }),
                leg({ legId: 'other', accountId: 'cat-utilities', amount: 100 }),
            ],
        });
        const posting = reminderBankPrefill(sameCurrency, {
            requestedSampleCount: 3, availableSampleCount: 3, amount: -120, unavailableReason: null,
        })!.prefill.postings![0]!;
        expect(posting).toMatchObject({ counterpartyAccountId: 'cat-utilities', amount: -120 });
    });
});

describe('reminderInvestmentDraft', () => {
    it('seeds a transfer template\'s other leg as the second amount, fixed', () => {
        const draft = reminderInvestmentDraft(detail({
            kind: 'investment', action: 'transfer', sourceAccountId: 'eur-broker',
            legs: [
                leg({ legId: 'b', accountId: 'eur-broker', amount: 90, postingRole: 'transfer' }),
                leg({ legId: 'x', accountId: 'usd-bank', amount: -100, postingRole: 'transfer' }),
            ],
        }), '2026-11-01')!.draft;
        expect(draft).toMatchObject({ amount: 90, transferAccountId: 'usd-bank', transferAmount: 100, transferAmountFixed: true });
    });
});
