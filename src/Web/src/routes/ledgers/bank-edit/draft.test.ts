import { describe, expect, it } from 'vitest';

import {
    seedCheckNumber,
    seedMemo,
    seedPayee,
    seedPostedAt,
    seedPostings,
    seedTags,
    seedTransactedAt,
} from './draft';
import type { TxnRowMode } from '../TxnRowEdit';

/**
 * Unit tests for the seeding logic, which previously lived in seven `useState`
 * initialisers and could only be reached by rendering the whole editor.
 *
 * `seedTransactedAt` is the one that earns this file. It carries a documented
 * data-loss history — an edit-mode caller that failed to seed the field would
 * silently WIPE a tax date the transaction already had — and its noise filter
 * has a subtlety the component-level gate cannot see: the existing same-day
 * test feeds BYTE-IDENTICAL ISO strings, so a naive `mode.transactedAt ===
 * mode.postedAt` passes it while being wrong for every realistic pair. The
 * cases below feed strings that differ.
 */

const BANK = '00000000-0000-0000-0000-000000000100';
const CAT = '00000000-0000-0000-0000-000000000300';

const newMode = (over: Partial<Extract<TxnRowMode, { kind: 'new' }>> = {}): TxnRowMode => ({
    kind: 'new',
    sourceAccountId: BANK,
    ...over,
});

const editMode = (over: Partial<Extract<TxnRowMode, { kind: 'edit' }>> = {}): TxnRowMode => ({
    kind: 'edit',
    headerId: '00000000-0000-0000-0000-000000000aaa',
    sourceAccountId: BANK,
    postings: [
        {
            legId: 'leg-1',
            counterpartyAccountId: CAT,
            counterpartyAccountName: 'Groceries',
            amount: -40.25,
            legMemo: null,
        },
    ],
    payee: 'Corner Shop',
    memo: null,
    checkNumber: null,
    postedAt: '2026-01-02T00:00:00.000Z',
    transactedAt: null,
    balanceAfter: 100,
    tags: [],
    needsReview: false,
    ...over,
});

describe('header seeds', () => {
    it('are blank in new mode with no prefill', () => {
        const m = newMode();
        expect(seedPayee(m)).toBe('');
        expect(seedMemo(m)).toBe('');
        expect(seedCheckNumber(m)).toBe('');
        expect(seedTags(m)).toEqual([]);
    });

    it('come from the prefill in new mode when Duplicate supplied one', () => {
        const m = newMode({
            prefill: { payee: 'Meridian', memo: 'September', checkNumber: '4471' },
        });
        expect(seedPayee(m)).toBe('Meridian');
        expect(seedMemo(m)).toBe('September');
        expect(seedCheckNumber(m)).toBe('4471');
    });

    it('turn edit-mode nulls into empty strings, not the string "null"', () => {
        const m = editMode({ payee: null, memo: null, checkNumber: null });
        expect(seedPayee(m)).toBe('');
        expect(seedMemo(m)).toBe('');
        expect(seedCheckNumber(m)).toBe('');
    });

    it('carry edit-mode values through', () => {
        const m = editMode({ payee: 'Corner Shop', memo: 'weekly', checkNumber: '99' });
        expect(seedPayee(m)).toBe('Corner Shop');
        expect(seedMemo(m)).toBe('weekly');
        expect(seedCheckNumber(m)).toBe('99');
        expect(seedTags(editMode({ tags: ['rental', 'q3'] }))).toEqual(['rental', 'q3']);
    });
});

describe('seedPostedAt', () => {
    it('uses the mode date in new mode when one is supplied', () => {
        // The reminders occurrence dialog passes the occurrence date so the
        // editor opens on it rather than today (ADR-0049).
        expect(seedPostedAt(newMode({ postedAt: '2026-03-15' }))).toBe('2026-03-15');
    });

    it('defaults to today in new mode with no date', () => {
        // Asserted by shape, not value — the point is that it is a real
        // date-input string rather than empty or an ISO stamp.
        expect(seedPostedAt(newMode())).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    });

    it('converts an edit-mode ISO stamp to a date-input value', () => {
        expect(seedPostedAt(editMode({ postedAt: '2026-01-02T12:00:00.000Z' }))).toBe('2026-01-02');
    });
});

describe('seedTransactedAt — the noise filter', () => {
    it('is empty in new mode', () => {
        expect(seedTransactedAt(newMode())).toBe('');
    });

    it('is empty when the row has no tax date', () => {
        expect(seedTransactedAt(editMode({ transactedAt: null }))).toBe('');
    });

    it('seeds a genuinely different day', () => {
        expect(seedTransactedAt(editMode({
            postedAt: '2026-01-02T12:00:00.000Z',
            transactedAt: '2026-03-15T12:00:00.000Z',
        }))).toBe('2026-03-15');
    });

    it('treats a same-day tax date as unset even when the STRINGS DIFFER', () => {
        // The case the component gate cannot catch: it feeds byte-identical
        // stamps, so a raw `===` passes it. These differ as strings and must
        // still normalise to the same calendar day.
        //
        // Both midday UTC so the local day is stable across any offset in
        // [-11, +10] — wide enough for CI without pinning TZ.
        expect(seedTransactedAt(editMode({
            postedAt: '2026-01-02T12:00:00.000Z',
            transactedAt: '2026-01-02T13:30:00.000Z',
        }))).toBe('');
    });

    it('is empty for an empty-string tax date, not just null', () => {
        // The guard is `!mode.transactedAt`, which deliberately catches both.
        expect(seedTransactedAt(editMode({ transactedAt: '' }))).toBe('');
    });
});

describe('seedPostings', () => {
    it('opens new mode with exactly one blank posting', () => {
        // A single row is just the N=1 case — there is no separate
        // "convert to split" path.
        const p = seedPostings(newMode());
        expect(p).toHaveLength(1);
        expect(p[0]).toMatchObject({ legId: null, counterpartyId: null, amount: '', legMemo: '' });
    });

    it('clones every prefilled posting, with no legIds', () => {
        const p = seedPostings(newMode({
            prefill: {
                postings: [
                    { counterpartyAccountId: CAT, amount: -4, legMemo: 'a' },
                    { counterpartyAccountId: CAT, amount: -3, legMemo: null },
                ],
            },
        }));
        expect(p).toHaveLength(2);
        // Duplicate must INSERT, never PATCH the original's legs.
        expect(p.every((x) => x.legId === null)).toBe(true);
        expect(p[0]?.amount).toBe('-4.00');
        expect(p[1]?.legMemo).toBe('');
    });

    it('preserves legIds and order in edit mode', () => {
        const p = seedPostings(editMode({
            postings: [
                { legId: 'l1', counterpartyAccountId: CAT, counterpartyAccountName: 'a', amount: -4, legMemo: null },
                { legId: 'l2', counterpartyAccountId: CAT, counterpartyAccountName: 'b', amount: -3, legMemo: 'x' },
            ],
        }));
        expect(p.map((x) => x.legId)).toEqual(['l1', 'l2']);
        expect(p.map((x) => x.amount)).toEqual(['-4.00', '-3.00']);
    });

    it('formats amounts to two decimals, because the field is free text', () => {
        const p = seedPostings(editMode({
            postings: [{ legId: 'l1', counterpartyAccountId: CAT, counterpartyAccountName: 'a', amount: -4, legMemo: null }],
        }));
        expect(p[0]?.amount).toBe('-4.00');
    });

    it('mints a fresh key per call, so it must run once per mount', () => {
        // Not idempotent by design — see postingDraft.ts. This is the property
        // that forces the call site to be useState, never useMemo.
        const a = seedPostings(editMode());
        const b = seedPostings(editMode());
        expect(a[0]?.key).not.toBe(b[0]?.key);
    });
});
