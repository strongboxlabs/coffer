import { describe, it, expect } from 'vitest';

import { formatBytes } from './format';

// One formatter for byte sizes. Separate local copies drift — different MB
// precision, a GB tier in one and not another — so the same size renders
// differently on two surfaces that sit side by side.

describe('formatBytes', () => {
    it('steps through the units', () => {
        expect(formatBytes(0)).toBe('0 B');
        expect(formatBytes(1023)).toBe('1023 B');
        expect(formatBytes(2048)).toBe('2.0 KB');
        expect(formatBytes(22.2 * 1024 * 1024)).toBe('22.2 MB');
    });

    it('keeps each caller’s MB precision', () => {
        // The import dialog asks for two; everyone else takes the default one.
        expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB');
        expect(formatBytes(5 * 1024 * 1024, 2)).toBe('5.00 MB');
    });

    it('renders GB rather than four-digit MB', () => {
        // A whole-database backup is the one thing here that can reach the GB
        // tier, and "2048.0 MB" is not
        // what anyone wants to read off a disaster-recovery screen.
        expect(formatBytes(2 * 1024 * 1024 * 1024)).toBe('2.00 GB');
        expect(formatBytes(1024 * 1024 * 1024 - 1)).toBe('1024.0 MB');
    });
});
