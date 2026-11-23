import { describe, expect, it } from 'vitest';

import {
    seedCheckNumber,
    seedIsCreate,
    seedMemo,
    seedNotesBaseline,
    seedPayee,
    seedPostedAt,
    seedPostings,
    seedTags,
    seedTransactedAt,
} from './draft';
import type { TxnRowMode } from '../TxnRowEdit';

/**
 * Unit tests for the seeding logic, reachable here without rendering the whole
 * editor.
 *
 * `seedTransactedAt` is the one that earns this file. An edit-mode caller that
 * fails to seed the field would silently WIPE a tax date the transaction already
 * had — and its noise filter
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
            counterpartyAccountName: 'Groceries', legTags: [],
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
    balanceCurrencyCode: 'USD',
    tags: [],
    inherited: null,
    needsReview: false,
    ...over,
});

/** The postings seeded for a source posting in `currency` (ADR-0114 D15). */
const seedPostingsIn = (currency: string | null, mode: TxnRowMode) => seedPostings(mode, currency);

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
            prefill: { payee: 'Payee A', memo: 'September', checkNumber: '4471', tags: ['Work'] },
        });
        expect(seedPayee(m)).toBe('Payee A');
        expect(seedMemo(m)).toBe('September');
        expect(seedCheckNumber(m)).toBe('4471');
        // A duplicate copies the tags the source showed, as the copy's own.
        expect(seedTags(m)).toEqual(['Work']);
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
        expect(seedTags(editMode({ tags: ['work', 'q3'] }))).toEqual(['work', 'q3']);
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
        const p = seedPostingsIn('USD', newMode());
        expect(p).toHaveLength(1);
        expect(p[0]).toMatchObject({ legId: null, counterpartyId: null, amount: '', legMemo: '' });
    });

    it('clones every prefilled posting, with no legIds', () => {
        const p = seedPostingsIn('USD', newMode({
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
        // At the source posting's places, as an edit seeds (ADR-0114 D15).
        expect(p[0]?.amount).toBe('-4.00');
        expect(p[1]?.legMemo).toBe('');
    });

    it('preserves legIds and order in edit mode', () => {
        const p = seedPostingsIn('USD', editMode({
            postings: [
                { legId: 'l1', counterpartyAccountId: CAT, counterpartyAccountName: 'a', legTags: [], amount: -4, legMemo: null },
                { legId: 'l2', counterpartyAccountId: CAT, counterpartyAccountName: 'b', legTags: [], amount: -3, legMemo: 'x' },
            ],
        }));
        expect(p.map((x) => x.legId)).toEqual(['l1', 'l2']);
        expect(p.map((x) => x.amount)).toEqual(['-4.00', '-3.00']);
    });

    // ADR-0114 D15: the field is free text, seeded at the posting currency's places.
    const whole = (currency: string | null, amount: number) => seedPostingsIn(currency, editMode({
        postings: [{ legId: 'l1', counterpartyAccountId: CAT, counterpartyAccountName: 'a', legTags: [], amount, legMemo: null }],
    }))[0]?.amount;

    it('seeds each amount at its currency\'s places, because the field is free text', () => {
        expect(whole('USD', -4)).toBe('-4.00');
        expect(whole('JPY', -4)).toBe('-4');
        expect(whole('KWD', -4)).toBe('-4.000');
        // No currency yet pads nothing.
        expect(whole(null, -4)).toBe('-4');
    });

    it('keeps every digit a stored figure carries beyond its currency\'s places', () => {
        // A stored figure finer than its currency is kept (ADR-0114 D15): the seed
        // shows it whole, and never rounds it to fit.
        expect(whole('USD', -4.125)).toBe('-4.125');
        expect(whole('JPY', -4.5)).toBe('-4.5');
    });

    it('remembers the text each amount was seeded with, so a save can tell a change', () => {
        const [p] = seedPostingsIn('JPY', editMode({
            postings: [{ legId: 'l1', counterpartyAccountId: CAT, counterpartyAccountName: 'a', legTags: [], amount: -4.5, legMemo: null }],
        }));
        expect(p?.seededAmount).toBe('-4.5');
        // A blank line was seeded with nothing.
        expect(seedPostingsIn('JPY', newMode())[0]?.seededAmount).toBeUndefined();
    });

    it('mints a fresh key per call, so it must run once per mount', () => {
        // Not idempotent by design — see postingDraft.ts. This is the property
        // that forces the call site to be useState, never useMemo.
        const a = seedPostingsIn('USD', editMode());
        const b = seedPostingsIn('USD', editMode());
        expect(a[0]?.key).not.toBe(b[0]?.key);
    });

    it('records each line\'s stored tag count, the size the limit measures growth from', () => {
        const p = seedPostingsIn('USD', editMode({
            postings: [{
                legId: 'l1', counterpartyAccountId: CAT, counterpartyAccountName: 'a',
                legTags: ['a', 'b', 'c'], amount: -4, legMemo: null,
            }],
        }));
        expect(p[0]?.tagBaseline).toBe(3);
    });
});

describe('seedNotesBaseline and the stored line values', () => {
    it('is what an edit opened with; each line carries its own stored values', () => {
        const mode = editMode({
            memo: 'Weekly shop',
            tags: ['Household'],
            postings: [
                { legId: 'l1', counterpartyAccountId: CAT, counterpartyAccountName: 'a', legTags: ['Organic'], amount: -4, legMemo: 'Produce' },
                { legId: 'l2', counterpartyAccountId: CAT, counterpartyAccountName: 'b', legTags: [], amount: -3, legMemo: null },
            ],
        });
        const b = seedNotesBaseline(mode);
        expect(b).toEqual({ memo: 'Weekly shop', tags: ['Household'] });
        expect(seedPostingsIn('USD', mode).map((p) => p.stored)).toEqual([
            { memo: 'Produce', tags: ['Organic'] },
            { memo: '', tags: [] },
        ]);
    });

    it('stores nothing for a copy, so every line counts as new', () => {
        const p = seedPostingsIn('USD', newMode({
            prefill: { postings: [{ counterpartyAccountId: CAT, amount: -4, legMemo: 'Produce', legTags: ['a'] }] },
        }));
        expect(p[0]?.stored).toBeNull();
        expect(p[0]?.tagBaseline).toBe(0);
        expect(seedIsCreate(newMode())).toBe(true);
    });

    it('treats a stored template\'s lines as stored, with no leg id when the prefill names none', () => {
        const mode = newMode({
            prefillIsStored: true,
            prefill: { postings: [{ counterpartyAccountId: CAT, amount: -4, legMemo: 'Produce', legTags: ['a'] }] },
        });
        const p = seedPostingsIn('USD', mode);
        expect(p[0]?.legId).toBeNull();
        expect(p[0]?.stored).toEqual({ memo: 'Produce', tags: ['a'] });
        expect(p[0]?.tagBaseline).toBe(1);
        expect(seedIsCreate(mode)).toBe(false);
    });

    it('marks a stored template line\'s tags as not known when the prefill does not carry them', () => {
        const p = seedPostingsIn('USD', newMode({
            prefillIsStored: true,
            prefill: { postings: [{ counterpartyAccountId: CAT, amount: -4, legMemo: 'Produce' }] },
        }));
        expect(p[0]?.stored).toEqual({ memo: 'Produce', tags: null });
    });
});
