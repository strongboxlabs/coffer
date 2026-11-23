import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import * as apiModule from '@/lib/api';
import { ApiError } from '@/lib/api';
import { problem, stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { TagDto } from '@/lib/types';

import { TagEditDialog } from './TagDialogs';

// Edit tag, saved with a name the server refuses. While the save is in flight
// Save reads "Saving…", keeps focus and refuses a second press from the mouse,
// Enter or Space, and Enter in the name field sends nothing either (ADR-0023 §L,
// pending.ts). After the refusal the dialog stays open with focus still on Save.
//
// Stubbed at fetch, so the dialog's own mutation runs; the PATCH is answered
// only when the test releases it. jsdom leaves focus on a control that becomes
// disabled, where a browser drops it to the page, so that Save is never natively
// disabled is asserted beside each focus check.

const LEDGER = 'ledger-1';
const TAG: TagDto = { id: 'tag-1', name: 'Tag A', color: null, usageCount: 2, transactionCount: 2 };
const TAKEN: TagDto = { id: 'tag-2', name: 'Tag B', color: null, usageCount: 1, transactionCount: 1 };
const PATCH = /^\/api\/ledgers\/ledger-1\/tags\/tag-1$/;

/** Holds every PATCH of the tag until `release`. The call is recorded as it is made. */
function holdPatch(stub: FetchStub) {
    let release!: () => void;
    const held = new Promise<void>((resolve) => { release = resolve; });
    const routed = globalThis.fetch;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
        const answer = routed(input, init);
        if (init?.method === 'PATCH' && stub.callsTo('PATCH', PATCH).length > 0) await held;
        return answer;
    });
    return release;
}

function renderEdit() {
    const onClose = vi.fn();
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    render(
        <QueryClientProvider client={client}>
            <TagEditDialog
                ledgerId={LEDGER}
                tag={TAG}
                allTags={[TAG, TAKEN]}
                onClose={onClose}
                onSaved={vi.fn()}
                onRequestMerge={vi.fn()}
            />
        </QueryClientProvider>,
    );
    return { onClose };
}

describe('TagEditDialog: a save the server refuses', () => {
    afterEach(() => vi.unstubAllGlobals());

    it('sends one PATCH for a second press, Enter, Space and Enter in the name, and keeps focus on Save', async () => {
        const stub = stubFetch().on('PATCH', PATCH, problem(422, 'A tag with that name exists.', 'tag-name-exists'));
        const release = holdPatch(stub);
        const user = userEvent.setup();
        const { onClose } = renderEdit();
        const dialog = screen.getByRole('dialog', { name: 'Edit tag' });
        const name = within(dialog).getByLabelText('Name');

        await user.clear(name);
        await user.type(name, 'Tag B');
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));
        const saving = await within(dialog).findByRole('button', { name: 'Saving…' });

        await user.click(saving);
        await user.keyboard('{Enter}');
        await user.keyboard(' ');
        expect(saving).toBeEnabled();
        expect(saving).toHaveFocus();
        await user.type(name, '{Enter}');
        expect(stub.callsTo('PATCH', PATCH)).toHaveLength(1);

        // The refusal: the dialog stays, says why, and Save still has focus.
        saving.focus();
        release();
        expect(await within(dialog).findByText('A tag named “Tag B” already exists.')).toBeInTheDocument();
        const save = await within(dialog).findByRole('button', { name: 'Save' });
        expect(save).toBe(saving);
        expect(save).toBeEnabled();
        expect(save).toHaveFocus();
        expect(onClose).not.toHaveBeenCalled();
        expect(stub.callsTo('PATCH', PATCH)).toHaveLength(1);
    });
});

// An error stays shown until the new result replaces or clears it (ADR-0023
// §L). The refusal sits above Save in a centred dialog: cleared at the press of
// a retry, the dialog would shrink and re-centre, and Save would move from
// under the pointer before the second press of a double click.
describe('TagEditDialog: a retry after a refusal', () => {
    afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

    it('keeps the refusal and its merge offer shown while the retry is in flight, and sends one PATCH for it', async () => {
        const patch = vi.spyOn(apiModule, 'patchTag')
            .mockRejectedValueOnce(new ApiError(422, 'A tag with that name exists.', 'tag-name-exists'))
            .mockImplementationOnce(() => new Promise(() => {}));
        const user = userEvent.setup();
        renderEdit();
        const dialog = screen.getByRole('dialog', { name: 'Edit tag' });
        const name = within(dialog).getByLabelText('Name');
        await user.clear(name);
        await user.type(name, 'Tag B');
        const save = within(dialog).getByRole('button', { name: 'Save' });
        await user.click(save);
        const refusal = await within(dialog).findByRole('alert');
        expect(refusal).toHaveTextContent('A tag named “Tag B” already exists.');

        // The retry, pressed twice back to back.
        fireEvent.click(save);
        fireEvent.click(save);
        await within(dialog).findByRole('button', { name: 'Saving…' });
        expect(within(dialog).getByRole('alert')).toBe(refusal);
        expect(refusal).toHaveTextContent('A tag named “Tag B” already exists.');
        expect(within(refusal).getByRole('button', { name: /Merge “Tag A” into “Tag B” instead/ })).toBeInTheDocument();
        expect(patch).toHaveBeenCalledTimes(2);
    });

    it('replaces the refusal with the one a refused press gives, without a request', async () => {
        const patch = vi.spyOn(apiModule, 'patchTag')
            .mockRejectedValueOnce(new ApiError(422, 'A tag with that name exists.', 'tag-name-exists'));
        const user = userEvent.setup();
        renderEdit();
        const dialog = screen.getByRole('dialog', { name: 'Edit tag' });
        const name = within(dialog).getByLabelText('Name');
        await user.clear(name);
        await user.type(name, 'Tag B');
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));
        await within(dialog).findByText('A tag named “Tag B” already exists.');

        await user.clear(name);
        await user.click(within(dialog).getByRole('button', { name: 'Save' }));
        expect(within(dialog).getByRole('alert')).toHaveTextContent('Name is required.');
        expect(within(dialog).queryByRole('button', { name: /instead/ })).toBeNull();
        expect(patch).toHaveBeenCalledTimes(1);
    });
});
