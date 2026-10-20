import { describe, it, expect } from 'vitest';
import { shouldRefreshOnSignal, shouldRepositionForDate } from './useRegisterController';

// The register sorts by posted DATE. After a saved edit the row is
// patched in place (preserving the user's scroll position) UNLESS the
// calendar date changed — then the row must relocate to its new slot,
// so the controller re-seeds the window. This guards that decision.
describe('shouldRepositionForDate', () => {
    it('repositions when the calendar date changed', () => {
        expect(
            shouldRepositionForDate('2026-06-01T12:00:00Z', '2026-07-15T00:00:00Z'),
        ).toBe(true);
    });

    it('does NOT reposition on a same-day time-only change', () => {
        expect(
            shouldRepositionForDate('2026-06-01T08:00:00Z', '2026-06-01T23:30:00Z'),
        ).toBe(false);
    });

    it('does NOT reposition when the date is unchanged', () => {
        expect(
            shouldRepositionForDate('2026-06-01T12:00:00Z', '2026-06-01T12:00:00Z'),
        ).toBe(false);
    });

    it('does NOT reposition when the old date is unknown (row not loaded)', () => {
        expect(shouldRepositionForDate(undefined, '2026-07-15T00:00:00Z')).toBe(false);
    });
});

// ADR-0079: the controller subscribes to the canonical ['register', …] key via a
// sentinel query and reloads the bespoke row window when it refetches. This
// guards the skip-initial logic — the tricky part — so an external invalidation
// reloads the rows while the window's own first load does not double-fetch.
describe('shouldRefreshOnSignal', () => {
    it('does NOT refresh before the sentinel has settled (updatedAt 0)', () => {
        expect(shouldRefreshOnSignal(null, 0)).toEqual({ refresh: false, nextSeen: null });
    });

    it('records the first settle WITHOUT refreshing (the initial window load is already fresh)', () => {
        expect(shouldRefreshOnSignal(null, 1000)).toEqual({ refresh: false, nextSeen: 1000 });
    });

    it('refreshes when the signal bumps — a writer invalidated the canonical key', () => {
        expect(shouldRefreshOnSignal(1000, 2000)).toEqual({ refresh: true, nextSeen: 2000 });
    });

    it('does NOT refresh on an unchanged signal (a re-render, same settle)', () => {
        expect(shouldRefreshOnSignal(2000, 2000)).toEqual({ refresh: false, nextSeen: 2000 });
    });
});
