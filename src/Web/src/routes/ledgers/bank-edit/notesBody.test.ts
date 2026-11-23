import { describe, expect, it } from 'vitest';

import { buildNotes, collapseNotes, notesIssues, type NotesBaseline } from './notesBody';
import type { PostingDraft } from './postingDraft';

const SIDE = 'acct-checking';

function posting(overrides: Partial<PostingDraft> & { key: string }): PostingDraft {
    return {
        legId: null,
        counterpartyId: 'acct-groceries',
        amount: '-10.00',
        legMemo: '',
        tags: [],
        tagBaseline: 0,
        stored: null,
        ...overrides,
    };
}

const EMPTY: NotesBaseline = { memo: '', tags: [] };

describe('buildNotes on an edit', () => {
    it('sends only the side when nothing changed', () => {
        const baseline: NotesBaseline = { memo: 'Team coffee', tags: ['Work'] };
        const notes = buildNotes({
            side: SIDE,
            isNew: false,
            memo: 'Team coffee',
            tags: ['Work'],
            postings: [posting({ key: 'p1', legId: 'leg-1' })],
            baseline,
        });
        expect(notes).toEqual({ side: SIDE });
    });

    it('sends nothing for an inheriting side left blank', () => {
        // The field is blank while this side inherits; leaving it blank must not
        // write anything, and must never copy the inherited value onto this side.
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [posting({ key: 'p1', legId: 'leg-1' })],
            baseline: EMPTY,
        });
        expect(notes).toEqual({ side: SIDE });
    });

    it('does not count trimming whitespace as a change', () => {
        const baseline: NotesBaseline = { memo: 'Team coffee', tags: [] };
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '  Team coffee ', tags: [],
            postings: [posting({ key: 'p1', legId: 'leg-1' })],
            baseline,
        });
        expect(notes).toEqual({ side: SIDE });
    });

    it('sends a typed memo, trimmed', () => {
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: ' Client lunch ', tags: [],
            postings: [posting({ key: 'p1', legId: 'leg-1' })],
            baseline: EMPTY,
        });
        expect(notes).toEqual({ side: SIDE, memo: 'Client lunch' });
    });

    it('sends null when an own memo is emptied, which reverts to inheriting', () => {
        const baseline: NotesBaseline = { memo: 'Client lunch', tags: ['Reimbursable'] };
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [posting({ key: 'p1', legId: 'leg-1' })],
            baseline,
        });
        expect(notes).toEqual({ side: SIDE, memo: null, tags: [] });
    });

    it('ignores tag order', () => {
        const baseline: NotesBaseline = { memo: '', tags: ['a', 'b'] };
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: ['b', 'a'],
            postings: [posting({ key: 'p1', legId: 'leg-1' })],
            baseline,
        });
        expect(notes).toEqual({ side: SIDE });
    });

    it('sends no lines on a one-posting result', () => {
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [posting({ key: 'p1', legId: 'leg-1', legMemo: 'stray' })],
            baseline: EMPTY,
        });
        expect(notes.lines).toBeUndefined();
    });

    it('sends only the lines that changed, by their position in the postings sent', () => {
        const baseline: NotesBaseline = { memo: 'Weekly shop', tags: ['Household'] };
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: 'Weekly shop', tags: ['Household'],
            postings: [
                posting({ key: 'p1', legId: 'leg-1', legMemo: 'Produce', tags: ['Organic'], stored: { memo: 'Produce', tags: ['Organic'] } }),
                posting({ key: 'p2', legId: 'leg-2', legMemo: 'Soap', tags: [], stored: { memo: '', tags: [] } }),
                posting({ key: 'p3', legMemo: '', tags: [] }),
            ],
            baseline,
        });
        expect(notes).toEqual({ side: SIDE, lines: [{ index: 1, memo: 'Soap' }] });
    });

    it('addresses a moved line by its new position', () => {
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [
                posting({ key: 'p2', legId: 'leg-2', legMemo: 'B', tags: ['x'], stored: { memo: 'B', tags: [] } }),
                posting({ key: 'p1', legId: 'leg-1', legMemo: 'A', stored: { memo: 'A', tags: [] } }),
            ],
            baseline: EMPTY,
        });
        expect(notes.lines).toEqual([{ index: 0, tags: ['x'] }]);
    });

    it('sends every stored line of a rebuilt template in full, cleared values as clears', () => {
        // The server rebuilds a reminder template's lines, so each line
        // restates what it holds: what the user saw is what is stored, however
        // the lines moved.
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [
                posting({ key: 'p1', legMemo: 'Produce', stored: { memo: 'Produce', tags: [] } }),
                posting({ key: 'p2', legMemo: '', tags: [], stored: { memo: 'Fuel', tags: ['Car'] } }),
            ],
            baseline: EMPTY,
            linesRebuilt: true,
        });
        expect(notes.lines).toEqual([
            { index: 0, memo: 'Produce', tags: [] },
            { index: 1, memo: null, tags: [] },
        ]);
    });

    it('sends the line that moved up after a middle line is removed with its own memo', () => {
        // Three template lines "a", "b", "c"; "b" removed. "c" is now at index
        // 1, where "b" was stored, so it must carry "c" itself.
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [
                posting({ key: 'p1', legMemo: 'a', stored: { memo: 'a', tags: null } }),
                posting({ key: 'p3', legMemo: 'c', stored: { memo: 'c', tags: null } }),
            ],
            baseline: EMPTY,
            linesRebuilt: true,
        });
        expect(notes.lines).toEqual([{ index: 0, memo: 'a' }, { index: 1, memo: 'c' }]);
    });

    it('sends a template line just added as blank, with no tags', () => {
        // "b" removed and a line added: the new line sits where "c" was
        // stored, so left out it would take "c"'s memo and tags.
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [
                posting({ key: 'p1', legMemo: 'a', stored: { memo: 'a', tags: null } }),
                posting({ key: 'p3', legMemo: 'c', stored: { memo: 'c', tags: null } }),
                posting({ key: 'p4' }),
            ],
            baseline: EMPTY,
            linesRebuilt: true,
        });
        expect(notes.lines).toEqual([
            { index: 0, memo: 'a' },
            { index: 1, memo: 'c' },
            { index: 2, memo: null, tags: [] },
        ]);
    });

    it('sends a template line\'s tags only when picked while what it stores is not known', () => {
        // A stored line seeded without its tags: an empty set there means
        // "not known", and sending it would clear what the line holds.
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: '', tags: [],
            postings: [
                posting({ key: 'p1', legMemo: 'Produce', stored: { memo: 'Produce', tags: null } }),
                posting({ key: 'p2', legMemo: 'Fuel', tags: ['Car'], stored: { memo: 'Fuel', tags: null } }),
            ],
            baseline: EMPTY,
            linesRebuilt: true,
        });
        expect(notes.lines).toEqual([
            { index: 0, memo: 'Produce' },
            { index: 1, memo: 'Fuel', tags: ['Car'] },
        ]);
    });

    it('always sends memo and tags after a collapse, even when they equal the stored transaction values', () => {
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: 'Weekly shop', tags: ['Household'],
            postings: [posting({ key: 'p1', legId: 'leg-1', stored: { memo: 'Produce', tags: ['Organic'] } })],
            baseline: { memo: 'Weekly shop', tags: ['Household'] },
            collapsed: true,
        });
        expect(notes).toEqual({ side: SIDE, memo: 'Weekly shop', tags: ['Household'] });
    });

    it('leaves the original line alone on a split, so the server copies this side\'s value to it', () => {
        // One-split being split: the original posting's own memo was the single
        // field's, so the line seeds blank and is left for the server.
        const notes = buildNotes({
            side: SIDE, isNew: false, memo: 'Weekly shop', tags: ['Household'],
            postings: [
                posting({ key: 'p1', legId: 'leg-1', stored: { memo: '', tags: [] } }),
                posting({ key: 'p2', legMemo: '', tags: [] }),
            ],
            baseline: { memo: 'Weekly shop', tags: ['Household'] },
        });
        expect(notes).toEqual({ side: SIDE });
    });
});

describe('buildNotes on a create', () => {
    it('sends every non-empty value', () => {
        const notes = buildNotes({
            side: SIDE, isNew: true, memo: 'Weekly shop', tags: ['Household'],
            postings: [
                posting({ key: 'p1', legMemo: 'Produce', tags: ['Organic'] }),
                posting({ key: 'p2', legMemo: '', tags: [] }),
            ],
            baseline: EMPTY,
        });
        expect(notes).toEqual({
            side: SIDE,
            memo: 'Weekly shop',
            tags: ['Household'],
            lines: [{ index: 0, memo: 'Produce', tags: ['Organic'] }],
        });
    });

    it('sends only the side for a blank create', () => {
        const notes = buildNotes({
            side: SIDE, isNew: true, memo: '  ', tags: [],
            postings: [posting({ key: 'p1' })],
            baseline: EMPTY,
        });
        expect(notes).toEqual({ side: SIDE });
    });
});

describe('collapseNotes', () => {
    it('joins the transaction memo in front of the remaining line\'s and unions the tags', () => {
        expect(collapseNotes(
            { memo: 'Weekly shop', tags: ['Household'] },
            posting({ key: 'p1', legMemo: 'Produce', tags: ['Organic'] }),
        )).toEqual({ memo: 'Weekly shop · Produce', tags: ['Household', 'Organic'] });
    });

    it('keeps one memo when the line has none', () => {
        expect(collapseNotes(
            { memo: 'Weekly shop', tags: [] },
            posting({ key: 'p1' }),
        )).toEqual({ memo: 'Weekly shop', tags: [] });
    });

    it('never drops a tag, even past the limit', () => {
        const header = Array.from({ length: 15 }, (_, i) => `h${i}`);
        const line = Array.from({ length: 10 }, (_, i) => `l${i}`);
        expect(collapseNotes({ memo: '', tags: header }, posting({ key: 'p1', tags: line })).tags)
            .toHaveLength(25);
    });
});

describe('notesIssues', () => {
    const many = (n: number) => Array.from({ length: n }, (_, i) => `t${i}`);

    it('accepts a set at the limit', () => {
        expect(notesIssues({ tags: many(20), tagBaseline: 0, postings: [] })).toEqual([]);
    });

    it('refuses a transaction set grown past the limit', () => {
        expect(notesIssues({ tags: many(21), tagBaseline: 20, postings: [] }))
            .toEqual(["This tag set can't grow past 20 tags."]);
    });

    it('accepts an over-limit set kept at its size', () => {
        expect(notesIssues({ tags: many(25), tagBaseline: 25, postings: [] })).toEqual([]);
    });

    it('names the posting whose set grew past the limit', () => {
        expect(notesIssues({
            tags: [],
            tagBaseline: 0,
            postings: [posting({ key: 'p1' }), posting({ key: 'p2', tags: many(21), tagBaseline: 3 })],
        })).toEqual(["Posting 2: this tag set can't grow past 20 tags."]);
    });

    it('checks posting sets only on a split', () => {
        expect(notesIssues({
            tags: [],
            tagBaseline: 0,
            postings: [posting({ key: 'p1', tags: many(21), tagBaseline: 3 })],
        })).toEqual([]);
    });
});
