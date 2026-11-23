import { describe, expect, it } from 'vitest';

import vectors from './annotation-vectors.json';
import {
    TAG_LIMIT,
    joinCollapsedMemo,
    normalizeMemo,
    ownMemo,
    ownTags,
    readOnlyBadge,
    sameTagSet,
    tagCountAccepted,
    unionTags,
} from './notes';

// The vector file is shared with the .NET tests, so the editor's pre-join and
// the server's write primitive are held to the same expectations. Every case in
// each family runs here; a family the client does not compute (name folding,
// ownership) is not read.

describe('memoJoin vectors', () => {
    it.each(vectors.memoJoin.map((v) => [v.name, v] as const))('%s', (_name, v) => {
        expect(joinCollapsedMemo(v.header, v.line)).toBe(v.expected);
    });
});

describe('tagUnion vectors', () => {
    it.each(vectors.tagUnion.map((v) => [v.name, v] as const))('%s', (_name, v) => {
        expect(unionTags(v.first, v.second)).toEqual(v.expected);
    });
});

describe('tagCap vectors', () => {
    it.each(vectors.tagCap.map((v) => [v.name, v] as const))('%s', (_name, v) => {
        const limit = v.limit as 'enforce' | 'systemMerge';
        expect(tagCountAccepted(v.stored, v.saved, limit)).toBe(v.accepted);
    });

    it('uses the same limit the server enforces', () => {
        expect(TAG_LIMIT).toBe(20);
    });
});

describe('blankMemo vectors', () => {
    it.each(vectors.blankMemo.map((v) => [v.name, v] as const))('%s', (_name, v) => {
        expect(normalizeMemo(v.input)).toBe(v.expected);
    });
});

describe('the side\'s own value', () => {
    const base = {
        memo: 'Team coffee',
        tags: ['Work'],
        memoInherited: false,
        tagsInherited: false,
    };

    it('is the shown value when the side owns it', () => {
        expect(ownMemo(base)).toBe('Team coffee');
        expect(ownTags(base)).toEqual(['Work']);
    });

    it('is blank when the shown value is inherited', () => {
        const inheriting = { ...base, memoInherited: true, tagsInherited: true };
        expect(ownMemo(inheriting)).toBeNull();
        expect(ownTags(inheriting)).toEqual([]);
    });

    it('treats memo and tags independently', () => {
        const mixed = { ...base, memoInherited: true };
        expect(ownMemo(mixed)).toBeNull();
        expect(ownTags(mixed)).toEqual(['Work']);
    });
});

describe('sameTagSet', () => {
    it('ignores order', () => {
        expect(sameTagSet(['a', 'b'], ['b', 'a'])).toBe(true);
    });

    it('compares names exactly', () => {
        expect(sameTagSet(['Work'], ['work'])).toBe(false);
    });

    it('sees a removed tag', () => {
        expect(sameTagSet(['a', 'b'], ['a'])).toBe(false);
    });
});

describe('readOnlyBadge', () => {
    it('names the investment counterparts', () => {
        expect(readOnlyBadge('eventCounterpartOne')).toBe('investment');
        expect(readOnlyBadge('eventOpposite')).toBe('investment');
    });

    it('names a split line seen from its other side', () => {
        expect(readOnlyBadge('splitOpposite')).toBe('split');
    });

    it('is absent on editable kinds', () => {
        expect(readOnlyBadge('onePosting')).toBe(null);
        expect(readOnlyBadge('splitSide')).toBe(null);
        expect(readOnlyBadge('eventSide')).toBe(null);
    });
});
