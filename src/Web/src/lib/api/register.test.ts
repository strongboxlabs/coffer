import { afterEach, describe, expect, it, vi } from 'vitest';

import { fetchRegister, fetchTransactionEntries, isRegisterFilterActive, type FetchRegisterArgs } from './register';
import { stubFetch } from '@/lib/annotations/fetchStub';

// A register page is always one account's (or one category with its
// descendants'): what a row shows depends on whose register it is in, so the
// server refuses a page without an account and this client cannot express one.
// The ledger-wide list is a different read, fetchTransactionEntries, which draws
// each entry as its own account's register does and names that account.
describe('fetchRegister', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    it('always names the account', async () => {
        const stub = stubFetch().on('GET', /\/transactions$/, { entries: [], cursorForOlder: null, cursorForNewer: null });
        await fetchRegister({ ledgerId: 'ledger-1', accountId: 'acct-1' });
        expect(stub.callsTo('GET', /\/transactions$/)[0]!.search.get('account_id')).toBe('acct-1');
    });

    // The type check of this file is the test. The other arguments are checked
    // on their own line, so the only error the directive can absorb is the
    // missing account.
    it('cannot be called without an account', () => {
        const withoutAccount: Omit<FetchRegisterArgs, 'accountId'> = { ledgerId: 'ledger-1' };
        // @ts-expect-error accountId is required: a register page has no ledger-wide form.
        const args: FetchRegisterArgs = withoutAccount;
        expect(args).toBe(withoutAccount);
    });
});

// The wire names are the ones the server binds, snake_case like the rest of the
// /transactions group; a misspelt one is silently ignored there, so the read would
// quietly widen.
describe('fetchTransactionEntries', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    const page = { entries: [], hasMore: false };

    it('sends needs_review, direction, today and limit by the names the server binds', async () => {
        const stub = stubFetch().on('GET', /\/transactions\/entries$/, page);
        await fetchTransactionEntries('ledger-1', { needsReview: true, direction: 'outflow', today: '2026-03-12', limit: 5 });
        const call = stub.callsTo('GET', /\/transactions\/entries$/)[0]!;
        expect(call.path).toBe('/api/ledgers/ledger-1/transactions/entries');
        expect([...call.search.entries()].sort()).toEqual([
            ['direction', 'outflow'], ['limit', '5'], ['needs_review', 'true'], ['today', '2026-03-12'],
        ]);
    });

    it('sends only the limit when nothing narrows the read', async () => {
        const stub = stubFetch().on('GET', /\/transactions\/entries$/, page);
        await fetchTransactionEntries('ledger-1', { needsReview: false, limit: 10 });
        const call = stub.callsTo('GET', /\/transactions\/entries$/)[0]!;
        expect([...call.search.entries()]).toEqual([['limit', '10']]);
    });
});

// isRegisterFilterActive is the single source of "is a user filter on?" — it
// drives the Filter button's active styling, the chips row, and both pages'
// match-count. Status/today are owned elsewhere (status tabs + controller), so
// they must NOT count as an active filter.
describe('isRegisterFilterActive', () => {
    it('is false for an empty filter', () => {
        expect(isRegisterFilterActive({})).toBe(false);
    });

    it('ignores status and today (owned by the tabs + controller)', () => {
        expect(isRegisterFilterActive({ status: 'cleared', today: '2026-07-13' })).toBe(false);
    });

    it('is true when any user dimension is set', () => {
        expect(isRegisterFilterActive({ search: 'grocer' })).toBe(true);
        expect(isRegisterFilterActive({ categoryId: 'cat-1' })).toBe(true);
        expect(isRegisterFilterActive({ securityId: 'sec-1' })).toBe(true);
        expect(isRegisterFilterActive({ tag: 'Project A' })).toBe(true);
        expect(isRegisterFilterActive({ dateFrom: '2026-01-01' })).toBe(true);
        expect(isRegisterFilterActive({ dateTo: '2026-12-31' })).toBe(true);
        expect(isRegisterFilterActive({ amountMax: 100 })).toBe(true);
    });

    it('treats an amount bound of 0 as active (0 is a real bound, not "unset")', () => {
        expect(isRegisterFilterActive({ amountMin: 0 })).toBe(true);
    });
});
