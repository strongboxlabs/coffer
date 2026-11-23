import { describe, expect, it } from 'vitest';

import type { LedgerInvestmentAction } from '@/lib/types';

import { draftOtherSide, inheritedAfterSave } from './inheritedAfterSave';

const CHECKING = 'acct-checking';
const SAVINGS = 'acct-savings';
const CATEGORY = 'acct-category';
const FEE_CATEGORY = 'acct-fee';

const draft = (over: Partial<Parameters<typeof draftOtherSide>[0]> = {}): Parameters<typeof draftOtherSide>[0] => ({
    action: 'transfer',
    categoryAccountId: null,
    transferAccountId: CHECKING,
    feeAccountId: null,
    feeAmount: null,
    ...over,
});

describe('draftOtherSide', () => {
    it('is the transfer account, in the transfer role, for a transfer, which never takes a fee posting', () => {
        expect(draftOtherSide(draft())).toEqual({ accountId: CHECKING, role: 'transfer' });
        expect(draftOtherSide(draft({ feeAccountId: FEE_CATEGORY, feeAmount: 2 })))
            .toEqual({ accountId: CHECKING, role: 'transfer' });
    });

    it.each(['dividend_cash', 'misc'] as const)('is the category, in the income role, for %s without a fee', (action) => {
        expect(draftOtherSide(draft({ action, categoryAccountId: CATEGORY, transferAccountId: null })))
            .toEqual({ accountId: CATEGORY, role: 'income' });
    });

    it.each(['dividend_cash', 'misc'] as const)('is null for %s with a fee: the save writes a second posting', (action) => {
        expect(draftOtherSide(draft({
            action, categoryAccountId: CATEGORY, transferAccountId: null, feeAccountId: FEE_CATEGORY, feeAmount: 2,
        }))).toBeNull();
    });

    it('counts a fee only with both its account and its amount, as the save does', () => {
        const misc = { action: 'misc' as const, categoryAccountId: CATEGORY, transferAccountId: null };
        expect(draftOtherSide(draft({ ...misc, feeAccountId: FEE_CATEGORY }))).toEqual({ accountId: CATEGORY, role: 'income' });
        expect(draftOtherSide(draft({ ...misc, feeAmount: 2 }))).toEqual({ accountId: CATEGORY, role: 'income' });
    });

    it('is null while the draft names no account across', () => {
        expect(draftOtherSide(draft({ transferAccountId: null }))).toBeNull();
        expect(draftOtherSide(draft({ action: 'misc', categoryAccountId: null }))).toBeNull();
        expect(draftOtherSide(draft({ action: null }))).toBeNull();
    });

    it.each<LedgerInvestmentAction>(['buy', 'sell', 'buyx', 'sellx', 'dividend_reinvest', 'divx', 'transfer_shares'])(
        'is null for %s: several postings, or one with the holdings account',
        (action) => {
            expect(draftOtherSide(draft({ action, categoryAccountId: CATEGORY }))).toBeNull();
        },
    );
});

describe('inheritedAfterSave (investment)', () => {
    const stored = { memo: 'Memo A', tags: ['Tag A'], fromAccountId: CHECKING, fromRole: 'transfer' as const };

    it('keeps the stored value while the draft saves one posting across from the same account, in the same role', () => {
        expect(inheritedAfterSave(stored, draft())).toBe(stored);
    });

    it('inherits nothing across another account: the stored leg is not kept, and its value goes with it', () => {
        expect(inheritedAfterSave(stored, draft({ transferAccountId: SAVINGS }))).toBeNull();
    });

    it('inherits nothing from the same account in another role', () => {
        const onCategory = { ...stored, fromAccountId: CATEGORY };
        expect(inheritedAfterSave(onCategory, draft({ action: 'misc', categoryAccountId: CATEGORY, transferAccountId: null })))
            .toBeNull();
        expect(inheritedAfterSave(onCategory, draft({ transferAccountId: CATEGORY }))).toBe(onCategory);
    });

    it('keeps a value on a leg without a role (a feed row\'s) in any role on its account', () => {
        const fed = { ...stored, fromRole: null };
        expect(inheritedAfterSave(fed, draft())).toBe(fed);
        const onCategory = { ...fed, fromAccountId: CATEGORY };
        expect(inheritedAfterSave(onCategory, draft({ action: 'misc', categoryAccountId: CATEGORY, transferAccountId: null })))
            .toBe(onCategory);
    });

    it('compares the account alone when the role is not known', () => {
        const { fromRole: _omitted, ...unknownRole } = stored;
        expect(inheritedAfterSave(unknownRole, draft())).toBe(unknownRole);
        expect(inheritedAfterSave(unknownRole, draft({ transferAccountId: SAVINGS }))).toBeNull();
    });

    it('inherits nothing while the draft names no account, or saves several postings', () => {
        expect(inheritedAfterSave(stored, draft({ transferAccountId: null }))).toBeNull();
        expect(inheritedAfterSave(stored, draft({ action: 'buyx' }))).toBeNull();
    });

    it('is null when nothing is stored, or the stored value comes from no side', () => {
        expect(inheritedAfterSave(null, draft())).toBeNull();
        expect(inheritedAfterSave({ memo: null, tags: [], fromAccountId: null, fromRole: null }, draft())).toBeNull();
    });
});
