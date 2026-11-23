import { afterEach, describe, expect, it, vi } from 'vitest';

import { stubFetch } from '@/lib/annotations/fetchStub';

import { saveBankEdit } from './save';

/**
 * The bank editor's one save: a body carrying a merge goes to the merge route
 * and nothing else; any other body is the PATCH. Mocked at `fetch`, so the
 * routes are the wire's.
 */

const LEDGER = '00000000-0000-0000-0000-000000000010';
const ACCOUNT = '00000000-0000-0000-0000-000000000100';
const EDITED = '00000000-0000-0000-0000-000000000aaa';
const SURVIVOR = '00000000-0000-0000-0000-000000000ccc';

const MERGE = /\/transactions\/[^/]+\/merge$/;
const PATCH = /\/transactions\/[^/]+$/;

afterEach(() => {
    vi.unstubAllGlobals();
});

describe('saveBankEdit', () => {
    it('folds the edited row into the picked candidate, with no PATCH', async () => {
        const stub = stubFetch().on('POST', MERGE, []).on('PATCH', PATCH, []);

        await saveBankEdit(LEDGER, EDITED, { mergeFromHeaderId: SURVIVOR }, ACCOUNT);

        const merges = stub.callsTo('POST', MERGE);
        expect(merges).toHaveLength(1);
        expect(merges[0]!.path).toBe(`/api/ledgers/${LEDGER}/transactions/${EDITED}/merge`);
        expect(merges[0]!.search.get('account_id')).toBe(ACCOUNT);
        expect(merges[0]!.body).toEqual({ fromHeaderId: SURVIVOR });
        expect(stub.callsTo('PATCH', PATCH)).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });

    it('PATCHes any other body, with no merge', async () => {
        const stub = stubFetch().on('POST', MERGE, []).on('PATCH', PATCH, []);

        await saveBankEdit(LEDGER, EDITED, { payee: 'Payee One', approve: true }, ACCOUNT);

        const patches = stub.callsTo('PATCH', PATCH);
        expect(patches).toHaveLength(1);
        expect(patches[0]!.path).toBe(`/api/ledgers/${LEDGER}/transactions/${EDITED}`);
        expect(patches[0]!.search.get('account_id')).toBe(ACCOUNT);
        expect(patches[0]!.body).toEqual({ payee: 'Payee One', approve: true });
        expect(stub.callsTo('POST', MERGE)).toEqual([]);
        expect(stub.unrouted).toEqual([]);
    });
});
