import { describe, expect, it } from 'vitest';

import { isRegisterFilterActive } from './register';

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
        expect(isRegisterFilterActive({ search: 'costco' })).toBe(true);
        expect(isRegisterFilterActive({ categoryId: 'cat-1' })).toBe(true);
        expect(isRegisterFilterActive({ securityId: 'sec-1' })).toBe(true);
        expect(isRegisterFilterActive({ tag: 'Property A' })).toBe(true);
        expect(isRegisterFilterActive({ dateFrom: '2026-01-01' })).toBe(true);
        expect(isRegisterFilterActive({ dateTo: '2026-12-31' })).toBe(true);
        expect(isRegisterFilterActive({ amountMax: 100 })).toBe(true);
    });

    it('treats an amount bound of 0 as active (0 is a real bound, not "unset")', () => {
        expect(isRegisterFilterActive({ amountMin: 0 })).toBe(true);
    });
});
