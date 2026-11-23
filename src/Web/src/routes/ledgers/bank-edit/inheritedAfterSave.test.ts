import { describe, expect, it } from 'vitest';

import { inheritedAfterSave } from './inheritedAfterSave';

const STORED_LEG = 'leg-stored';
const STORED_SIDE = 'acct-stored';
const DRAFTED_SIDE = 'acct-drafted';
const stored = { memo: 'Memo A', tags: ['Tag A'], fromAccountId: STORED_SIDE };

describe('inheritedAfterSave (bank)', () => {
    it('keeps the stored value from the stored side while the posting and its counterparty are unchanged', () => {
        expect(inheritedAfterSave(stored, STORED_LEG, { legId: STORED_LEG, counterpartyId: STORED_SIDE }))
            .toEqual(stored);
    });

    it('names the counterparty the draft has: the kept posting\'s other leg moves there with its value', () => {
        expect(inheritedAfterSave(stored, STORED_LEG, { legId: STORED_LEG, counterpartyId: DRAFTED_SIDE }))
            .toEqual({ memo: 'Memo A', tags: ['Tag A'], fromAccountId: DRAFTED_SIDE });
    });

    it('names no side while the field names none', () => {
        expect(inheritedAfterSave(stored, STORED_LEG, { legId: STORED_LEG, counterpartyId: null }))
            .toEqual({ memo: 'Memo A', tags: ['Tag A'], fromAccountId: null });
    });

    it('inherits nothing through a posting that replaced the stored one: its legs are new and carry no value', () => {
        expect(inheritedAfterSave(stored, STORED_LEG, { legId: null, counterpartyId: STORED_SIDE }))
            .toEqual({ memo: null, tags: [], fromAccountId: STORED_SIDE });
    });

    it('is null when nothing is stored to inherit', () => {
        expect(inheritedAfterSave(null, STORED_LEG, { legId: STORED_LEG, counterpartyId: STORED_SIDE })).toBeNull();
    });
});
