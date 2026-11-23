import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query';

import { ConfirmDialog } from './ConfirmDialog';

// A confirm whose request is in flight reads "Working…", keeps focus and
// refuses a second press (ADR-0023 §L, pending.ts). The host here runs a real
// mutation, held until the test answers it, as a page's confirm does.
//
// jsdom leaves focus on a control that becomes disabled, where a browser drops
// it to the page; that the button is never natively disabled is asserted beside
// each focus check, since it is what keeps focus there in the browser.

/** A request the test answers: `refuse()` fails it, as a server refusal does. */
function heldRequest() {
    let refuse!: () => void;
    const send = vi.fn(() => new Promise<void>((_, reject) => {
        refuse = () => reject(new Error('Refused.'));
    }));
    return { send, refuse: () => refuse() };
}

function Host({ send, phrase }: { send: () => Promise<void>; phrase?: string }) {
    const [open, setOpen] = useState(true);
    const remove = useMutation({ mutationFn: send, onSuccess: () => setOpen(false) });
    return (
        <ConfirmDialog
            open={open}
            title="Remove it?"
            confirmLabel="Remove"
            variant="danger"
            requireTypedConfirmation={phrase}
            isConfirming={remove.isPending}
            onConfirm={() => remove.mutate()}
            onCancel={() => setOpen(false)}
        />
    );
}

/** Lets microtasks run and no task: the request is sent, and TanStack has not yet told the page. */
const ticks = async () => { for (let i = 0; i < 10; i += 1) await Promise.resolve(); };

function renderHost(send: () => Promise<void>, phrase?: string) {
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    return render(<QueryClientProvider client={client}><Host send={send} phrase={phrase} /></QueryClientProvider>);
}

describe('ConfirmDialog while its request is in flight', () => {
    it('sends one request for a second press, Enter and Space, and keeps focus on Confirm through a refusal', async () => {
        const { send, refuse } = heldRequest();
        const user = userEvent.setup();
        renderHost(send);
        const dialog = screen.getByRole('dialog', { name: 'Remove it?' });

        await user.click(within(dialog).getByRole('button', { name: 'Remove' }));
        const working = await within(dialog).findByRole('button', { name: 'Working…' });
        expect(working).toHaveAttribute('aria-busy', 'true');

        await user.click(working);
        await user.keyboard('{Enter}');
        await user.keyboard(' ');
        expect(send).toHaveBeenCalledTimes(1);
        expect(working).toBeEnabled();
        expect(working).toHaveFocus();

        refuse();
        const again = await within(dialog).findByRole('button', { name: 'Remove' });
        expect(again).toBe(working);
        expect(again).not.toHaveAttribute('aria-busy');
        expect(again).toHaveFocus();
        expect(screen.getByRole('dialog', { name: 'Remove it?' })).toBeInTheDocument();
    });

    // Back to back, before TanStack's notify timeout has let isConfirming
    // render: only the press's own hold refuses the second (pending.ts).
    it('sends one request for a double press, and for Space pressed twice, before Working… renders', async () => {
        const { send, refuse } = heldRequest();
        renderHost(send);
        const remove = within(screen.getByRole('dialog', { name: 'Remove it?' })).getByRole('button', { name: 'Remove' });

        fireEvent.click(remove);
        fireEvent.click(remove);
        await ticks();
        expect(remove).toHaveAccessibleName('Remove');
        expect(send).toHaveBeenCalledTimes(1);

        await waitFor(() => expect(remove).toHaveAccessibleName('Working…'));
        refuse();
        await waitFor(() => expect(remove).toHaveAccessibleName('Remove'));
        const user = userEvent.setup({ delay: null });
        remove.focus();
        await user.keyboard('  ');
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
    });

    it('sends one request for Enter pressed twice in the typed confirmation before Working… renders', async () => {
        const { send } = heldRequest();
        const user = userEvent.setup({ delay: null });
        renderHost(send, 'delete 3');
        await user.type(screen.getByRole('textbox'), 'delete 3{Enter}{Enter}');
        await ticks();
        expect(send).toHaveBeenCalledTimes(1);
    });

    it('sends one request for Enter pressed again in the typed confirmation', async () => {
        const { send } = heldRequest();
        const user = userEvent.setup();
        renderHost(send, 'delete 3');
        const field = screen.getByRole('textbox');

        await user.type(field, 'delete 3{Enter}');
        expect(await screen.findByRole('button', { name: 'Working…' })).toBeInTheDocument();
        await user.type(field, '{Enter}');
        await user.click(screen.getByRole('button', { name: 'Working…' }));
        expect(send).toHaveBeenCalledTimes(1);
    });
});
