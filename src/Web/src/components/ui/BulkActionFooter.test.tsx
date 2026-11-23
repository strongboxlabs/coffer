import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/react';

import { BulkActionFooter } from './BulkActionFooter';

// The footer names every selected row, read-only rows included, and the
// read-only part beside the count. Each case reads the count element by its
// own text and compares it whole, so a missing or extra part fails.
function countText(): string | null {
    const region = screen.getByRole('region', { name: 'Bulk actions' });
    return within(region).getByText(/\d+ selected/).textContent;
}

describe('BulkActionFooter', () => {
    it('reads "N selected" when no selected row is read-only', () => {
        render(<BulkActionFooter selectedCount={4} readOnlyCount={0} />);
        expect(countText()).toBe('4 selected');
    });

    it('reads "N selected" when the read-only count is not given', () => {
        render(<BulkActionFooter selectedCount={4} />);
        expect(countText()).toBe('4 selected');
    });

    it('names the read-only part beside the count', () => {
        render(<BulkActionFooter selectedCount={15} readOnlyCount={3} />);
        expect(countText()).toBe('15 selected · 3 read-only');
    });

    it('counts a selection that is all read-only', () => {
        render(<BulkActionFooter selectedCount={2} readOnlyCount={2} />);
        expect(countText()).toBe('2 selected · 2 read-only');
    });
});
