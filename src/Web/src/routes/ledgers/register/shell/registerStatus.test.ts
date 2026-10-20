import { describe, expect, it } from 'vitest';
import type { StatusFilter } from './registerStatus';
import { toSelectionStatusFilter } from './registerStatus';

// Regression: 'needs_review' used to map to the wire's 'all' filter
// because the bulk-selection contract had no needs_review predicate —
// so a select-all on the "Needs review" tab silently widened to the
// whole account. The wire now models needs_review, so every UI filter
// maps to itself (the mapping is a type-safe pass-through boundary).
describe('toSelectionStatusFilter', () => {
    const filters: StatusFilter[] = [
        'all',
        'cleared',
        'uncleared',
        'reconciling',
        'scheduled',
        'needs_review',
        'hidden',
    ];

    it.each(filters)('passes %s through unchanged', (filter) => {
        expect(toSelectionStatusFilter(filter)).toBe(filter);
    });

    it('does not widen needs_review to all', () => {
        expect(toSelectionStatusFilter('needs_review')).not.toBe('all');
    });
});
