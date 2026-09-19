import { describe, expect, it } from 'vitest';

import { postingIssues, validatePostings } from './validation';
import { emptyDraft, prefillToDraft, seedToDraft, type PostingDraft } from './postingDraft';

/**
 * The point of extracting the pure tier: these rules used to need a 1,600-line
 * render to exercise at all. Structured to match
 * `investment-edit/validation.test.ts` so a reviewer reads both the same way —
 * a complete-object factory spread-overridden per case, a happy path asserted
 * as empty, and messages matched by regex rather than exact string so a copy
 * edit does not fail the suite.
 */

const draft = (over: Partial<PostingDraft> = {}): PostingDraft => ({
    key: 'k1',
    legId: null,
    counterpartyId: 'cat-1',
    amount: '-12.50',
    legMemo: '',
    ...over,
});

describe('validatePostings', () => {
    it('accepts a complete posting', () => {
        expect(validatePostings([draft()])).toEqual([]);
    });

    it('accepts ZERO — a paycheck split carries zero lines', () => {
        // Deliberately legal on both client and server; the DB has no non-zero
        // constraint. Before the extraction nothing pinned this at unit level.
        expect(validatePostings([draft({ amount: '0' })])).toEqual([]);
        expect(validatePostings([draft({ amount: '0.00' })])).toEqual([]);
        expect(validatePostings([draft({ amount: '-0' })])).toEqual([]);
    });

    it('accepts a split whose postings do not sum to anything in particular', () => {
        // ADR-0025 rejected auto-balance and sum warnings outright. An
        // "unbalanced" split is not an invalid one, and a future keyed-map
        // refactor must not quietly introduce a balance rule.
        const odd = [
            draft({ key: 'a', amount: '100' }),
            draft({ key: 'b', amount: '7.13' }),
            draft({ key: 'c', amount: '-3' }),
        ];
        expect(validatePostings(odd)).toEqual([]);
    });

    it('requires at least one posting', () => {
        expect(validatePostings([])).toEqual([expect.stringMatching(/at least one posting/i)]);
    });

    describe('amount', () => {
        it('rejects blank and whitespace', () => {
            expect(validatePostings([draft({ amount: '' })]))
                .toEqual([expect.stringMatching(/amount is required/i)]);
            expect(validatePostings([draft({ amount: '   ' })]))
                .toEqual([expect.stringMatching(/amount is required/i)]);
        });

        it('rejects unparseable text', () => {
            expect(validatePostings([draft({ amount: 'abc' })]))
                .toEqual([expect.stringMatching(/amount is required/i)]);
        });
    });

    describe('counterparty', () => {
        it('rejects a posting with none picked', () => {
            expect(validatePostings([draft({ counterpartyId: null })]))
                .toEqual([expect.stringMatching(/pick a counterparty/i)]);
        });
    });

    describe('message shape — why this is a list and not a keyed map', () => {
        it('names the posting by its 1-based index', () => {
            const issues = validatePostings([
                draft({ key: 'a' }),
                draft({ key: 'b', amount: '' }),
            ]);
            expect(issues).toEqual([expect.stringMatching(/^Posting 2:/)]);
        });

        it('emits BOTH problems for one posting, in construction order', () => {
            // This is the reason the return type is a string list. A map keyed
            // by field collapses these two into one and loses their order, and
            // the shell renders them verbatim as joined text — so converting
            // would be a user-visible change, not a refactor.
            const issues = validatePostings([draft({ amount: '', counterpartyId: null })]);
            expect(issues).toHaveLength(2);
            expect(issues[0]).toMatch(/Posting 1: amount is required/i);
            expect(issues[1]).toMatch(/Posting 1: pick a counterparty/i);
        });

        it('keeps per-posting messages separate across postings', () => {
            const issues = validatePostings([
                draft({ key: 'a', amount: '' }),
                draft({ key: 'b' }),
                draft({ key: 'c', counterpartyId: null }),
            ]);
            expect(issues).toHaveLength(2);
            expect(issues[0]).toMatch(/^Posting 1:/);
            expect(issues[1]).toMatch(/^Posting 3:/);
        });
    });

    describe('against the real inverters', () => {
        it('passes a draft built from an existing leg', () => {
            const seeded = seedToDraft({
                legId: 'leg-1',
                counterpartyAccountId: 'cat-1',
                amount: -40.25,
                legMemo: null,
            });
            expect(seeded.amount).toBe('-40.25');
            expect(validatePostings([seeded])).toEqual([]);
        });

        it('passes a draft built from a duplicate prefill', () => {
            const filled = prefillToDraft({ counterpartyAccountId: 'cat-1', amount: 12 });
            // A prefill has no legId — the server INSERTs it.
            expect(filled.legId).toBeNull();
            expect(validatePostings([filled])).toEqual([]);
        });

        it('fails a freshly added blank posting on BOTH counts', () => {
            // `emptyDraft` seeds no amount and no counterparty, so the ghost
            // row a user just materialised is invalid until they fill it.
            expect(validatePostings([emptyDraft()])).toHaveLength(2);
        });
    });

    describe('key generation', () => {
        it('mints a distinct key per draft, which is why construction is not idempotent', () => {
            // Load-bearing: `key` is the React key for a leg row and must be
            // stable across a reorder, so it cannot derive from array position
            // or from legId (a new posting has none). The consequence is that
            // the draft hook must capture `initial` ONCE — a recomputed initial
            // carries different keys from the live draft, latching `dirty` true
            // and making reset() remount every row.
            const keys = [emptyDraft().key, emptyDraft().key, emptyDraft().key];
            expect(new Set(keys).size).toBe(3);
        });
    });
});

describe('postingIssues — the structured form the splits grid marks rows from', () => {
    it('is empty when every posting is complete', () => {
        expect(postingIssues([draft(), draft({ key: 'k2' })])).toEqual([]);
    });

    it('carries the row number and the posting key, not just a sentence', () => {
        // Both are load-bearing: `n` is what the grid prints in its # column
        // and what the summary strip links to, `key` is how a row finds its
        // own issues without an index lookup that a reorder would invalidate.
        const issues = postingIssues([
            draft({ key: 'a' }),
            draft({ key: 'b', amount: '' }),
        ]);
        expect(issues).toHaveLength(1);
        expect(issues[0]).toMatchObject({ key: 'b', n: 2, kind: 'amount' });
    });

    it('reports BOTH defects of one posting, in rule order', () => {
        // The reason validatePostings could never be a field-keyed map: a
        // freshly added row is missing an amount AND a counterparty, and
        // collapsing those to one message hides half the work.
        const issues = postingIssues([emptyDraft()]);
        expect(issues.map((i) => i.kind)).toEqual(['amount', 'counterparty']);
    });

    it('numbers by position, so a reorder renumbers the messages', () => {
        // The grid's # column and the message's prefix are the same number by
        // construction. If they could disagree, the summary strip would point
        // at the wrong row — the failure the row numbers exist to prevent.
        const bad = draft({ key: 'bad', counterpartyId: null });
        const good = draft({ key: 'good' });
        expect(postingIssues([good, bad])[0]?.n).toBe(2);
        expect(postingIssues([bad, good])[0]?.n).toBe(1);
    });

    it('agrees with validatePostings message for message', () => {
        // One rule set, two shapes. A row marking itself red while the Save
        // tooltip disagrees about why is what two parallel validators produce.
        const postings = [emptyDraft(), draft({ key: 'k2' }), draft({ key: 'k3', amount: 'abc' })];
        expect(postingIssues(postings).map((i) => i.message))
            .toEqual(validatePostings(postings));
    });

    it('has no entry for the empty list, which is a statement about the list', () => {
        // validatePostings still says "Add at least one posting." There is no
        // posting to attach that to, so it is deliberately not an issue.
        expect(postingIssues([])).toEqual([]);
        expect(validatePostings([])).toEqual(['Add at least one posting.']);
    });

    it('gives every issue a hint short enough for a marker tooltip', () => {
        // The per-row marker shows `hint`, not `message`: the row already
        // says which number it is, so repeating "Posting 4:" in a tooltip
        // anchored to row 4 is noise.
        for (const issue of postingIssues([emptyDraft()])) {
            expect(issue.hint.length).toBeLessThan(40);
            expect(issue.hint).not.toMatch(/Posting \d/);
        }
    });
});
