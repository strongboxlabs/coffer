import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, fireEvent, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { TagsPanel } from './TagsPanel';
import * as apiModule from '@/lib/api';
import { ApiError } from '@/lib/api';
import type { TagDto } from '@/lib/types';
import {
    LEDGER_ID,
    renderInRouter,
    seedRole,
    testQueryClient,
} from '../structure/structureTestFixtures';

// The Tags tab of Structure (ADR-0111):
//   * Lists tags with how many transactions carry each; empty state; no matches.
//   * Search narrows the list; a name opens that tag's transactions.
//   * "Remove N unused" appears only with orphans and calls cleanup.
//   * A VISIBLE per-row actions button opens the same menu as right-click, so no action is
//     right-click-only.
//   * New tag makes a tag; a reader who cannot edit is offered none of it.

const tag = (over: Partial<TagDto> & { id: string; name: string }): TagDto => ({
    color: null, usageCount: 0, transactionCount: 0, ...over,
});

function renderPanel(tags: TagDto[], role = 'owner') {
    const queryClient = testQueryClient();
    queryClient.setQueryData(['tags', LEDGER_ID], tags);
    seedRole(queryClient, role);
    // A mutation refetches the list; give it the list it was seeded with.
    vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(tags);
    return { queryClient, ...renderInRouter(<TagsPanel ledgerId={LEDGER_ID} />, queryClient) };
}

describe('TagsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('lists tags with how many transactions carry each', async () => {
        renderPanel([
            tag({ id: 't1', name: 'work', color: '#3b82f6', usageCount: 5, transactionCount: 3 }),
            tag({ id: 't2', name: 'home', transactionCount: 1, usageCount: 1 }),
        ]);

        expect(await screen.findByText('work')).toBeInTheDocument();
        expect(screen.getByText('home')).toBeInTheDocument();
        // The distinct transactions, not the pairings (a transaction tagged twice is one).
        expect(screen.getByText('3 transactions')).toBeInTheDocument();
        expect(screen.getByText('1 transaction')).toBeInTheDocument();
        expect(screen.queryByText('5 transactions')).not.toBeInTheDocument();
    });

    it('opens a tag’s transactions from its name', async () => {
        renderPanel([tag({ id: 't1', name: 'work', transactionCount: 3, usageCount: 3 })]);

        const link = await screen.findByRole('link', { name: 'work' });
        expect(link).toHaveAttribute('href', `/ledgers/${LEDGER_ID}/tags/t1`);
        await userEvent.click(link);
        expect(await screen.findByText('tag page')).toBeInTheDocument();
    });

    it('renders the empty state, with a way to make the first tag, when there are none', async () => {
        renderPanel([]);
        expect(await screen.findByText('No tags yet')).toBeInTheDocument();
        expect(screen.getByText(/Tags are labels you can put on any transaction/)).toBeInTheDocument();
        // The toolbar's button and the empty state's: two ways to the same dialog.
        expect(screen.getAllByRole('button', { name: /New tag/ })).toHaveLength(2);
    });

    it('offers no way to make a tag to a reader who cannot edit', async () => {
        renderPanel([], 'viewer');
        expect(await screen.findByText('No tags yet')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /New tag/ })).not.toBeInTheDocument();
    });

    it('shows "Remove unused" only with orphans and calls cleanup', async () => {
        const tags = [
            tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 }),
            tag({ id: 't2', name: 'stale' }),
        ];
        const cleanup = vi.spyOn(apiModule, 'cleanupUnusedTags').mockResolvedValue({ tagsRemoved: 1 });
        renderPanel(tags);

        const btn = await screen.findByRole('button', { name: /Remove 1 unused/i });
        fireEvent.click(btn);
        await waitFor(() => expect(cleanup).toHaveBeenCalledWith(LEDGER_ID));
    });

    it('gives focus to New tag when the cleanup it ran takes "Remove unused" away', async () => {
        // ADR-0023 §L: a control that removes itself hands focus on, never to the page.
        const used = tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 });
        let tags = [used, tag({ id: 't2', name: 'stale' })];
        renderPanel(tags);
        vi.spyOn(apiModule, 'fetchTags').mockImplementation(async () => tags);
        vi.spyOn(apiModule, 'cleanupUnusedTags').mockImplementation(async () => {
            tags = [used];
            return { tagsRemoved: 1 };
        });
        const user = userEvent.setup();

        const btn = await screen.findByRole('button', { name: /Remove 1 unused/i });
        btn.focus();
        await user.keyboard('{Enter}');

        await waitFor(() => expect(screen.queryByText('stale')).not.toBeInTheDocument());
        expect(screen.queryByRole('button', { name: /Remove .* unused/i })).not.toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'New tag' })).toHaveFocus();
    });

    it('hides "Remove unused" when every tag is in use', async () => {
        renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);

        await screen.findByText('work');
        expect(screen.queryByRole('button', { name: /Remove .* unused/i })).not.toBeInTheDocument();
    });

    it('narrows the list as the search box is typed in, and says so when nothing matches', async () => {
        renderPanel([
            tag({ id: 't1', name: 'Holiday', usageCount: 2, transactionCount: 2 }),
            tag({ id: 't2', name: 'Work', usageCount: 1, transactionCount: 1 }),
        ]);
        await screen.findByText('Holiday');

        await userEvent.type(screen.getByRole('searchbox', { name: 'Search tags' }), 'hol');
        expect(screen.getByText('Holiday')).toBeInTheDocument();
        expect(screen.queryByText('Work')).not.toBeInTheDocument();

        await userEvent.clear(screen.getByRole('searchbox', { name: 'Search tags' }));
        await userEvent.type(screen.getByRole('searchbox', { name: 'Search tags' }), 'zzz');
        expect(await screen.findByText('Nothing matches “zzz”')).toBeInTheDocument();
        expect(screen.queryByText('Holiday')).not.toBeInTheDocument();

        await userEvent.click(screen.getByRole('button', { name: 'Clear search' }));
        expect(await screen.findByText('Holiday')).toBeInTheDocument();
        expect(screen.getByText('Work')).toBeInTheDocument();
    });

    it('gives every row a visible actions button', async () => {
        // Without a visible button, edit/merge/delete would be reachable only by right-clicking,
        // advertised by a title tooltip that does not exist on touch and is not announced as an
        // affordance.
        renderPanel([
            tag({ id: 't1', name: 'work', color: '#3b82f6', usageCount: 3, transactionCount: 3 }),
            tag({ id: 't2', name: 'home', usageCount: 1, transactionCount: 1 }),
        ]);
        expect(await screen.findByRole('button', { name: 'Actions for work' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Actions for home' })).toBeInTheDocument();
    });

    it('opens the same menu from the button as from right-click', async () => {
        renderPanel([
            tag({ id: 't1', name: 'work', color: '#3b82f6', usageCount: 3, transactionCount: 3 }),
            tag({ id: 't2', name: 'home', usageCount: 1, transactionCount: 1 }),
        ]);

        // Anchor: the menu is absent before the click, so the assertion after it cannot pass
        // on a menu that was always there.
        expect(screen.queryByText('Open transactions')).not.toBeInTheDocument();

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for work' }));
        const fromButton = (await screen.findAllByRole('menuitem')).map((i) => i.textContent);
        expect(fromButton).toEqual(['Open transactions', 'Edit…', 'Merge…', 'Delete']);
        fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' });
        await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());

        fireEvent.contextMenu(screen.getByText('work'));
        const fromRightClick = (await screen.findAllByRole('menuitem')).map((i) => i.textContent);
        expect(fromRightClick).toEqual(fromButton);
    });

    it('leaves Merge out of reach when there is no other tag to merge into', async () => {
        renderPanel([tag({ id: 't1', name: 'only', usageCount: 1, transactionCount: 1 })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for only' }));

        expect(await screen.findByRole('menuitem', { name: 'Merge…' })).toBeDisabled();
    });

    it('goes to a tag’s transactions from its menu', async () => {
        renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for work' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Open transactions' }));

        expect(await screen.findByText('tag page')).toBeInTheDocument();
    });

    it('draws no row actions, no New tag, no Remove unused and no menu for a viewer', async () => {
        // The server refuses every write from a reader who cannot edit. Paired with the tests
        // above: the same rows draw the button, the menu and "Remove 1 unused" for an owner.
        renderPanel([
            tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 }),
            tag({ id: 't2', name: 'stale' }),
        ], 'viewer');

        expect(await screen.findByText('work')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Actions for work' })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /Remove .* unused/i })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /New tag/ })).not.toBeInTheDocument();
        fireEvent.contextMenu(screen.getByText('work'));
        expect(screen.queryByRole('menu')).not.toBeInTheDocument();
        // Reading still works: the name still opens the tag.
        expect(screen.getByRole('link', { name: 'work' })).toBeInTheDocument();
    });

    // ----- New tag ----------------------------------------------------------------

    it('makes a tag from the New tag dialog, with no colour unless one is picked', async () => {
        const create = vi.spyOn(apiModule, 'createTag')
            .mockResolvedValue(tag({ id: 'new', name: 'Trip' }));
        renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);

        await userEvent.click(await screen.findByRole('button', { name: /New tag/ }));
        const dialog = await screen.findByRole('dialog');
        // No colour is the starting choice, and says so.
        expect(within(dialog).getByRole('button', { name: 'No colour' })).toHaveAttribute('aria-pressed', 'true');
        await userEvent.type(within(dialog).getByLabelText('Name'), '  Trip ');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

        await waitFor(() => expect(create).toHaveBeenCalledWith(LEDGER_ID, { name: 'Trip' }));
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    });

    it('sends the colour that was picked', async () => {
        const create = vi.spyOn(apiModule, 'createTag')
            .mockResolvedValue(tag({ id: 'new', name: 'Trip', color: '#ef4444' }));
        renderPanel([]);

        await userEvent.click((await screen.findAllByRole('button', { name: /New tag/ }))[0]!);
        const dialog = await screen.findByRole('dialog');
        await userEvent.type(within(dialog).getByLabelText('Name'), 'Trip');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Colour #ef4444' }));
        expect(within(dialog).getByRole('button', { name: 'No colour' })).toHaveAttribute('aria-pressed', 'false');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

        await waitFor(() => expect(create).toHaveBeenCalledWith(LEDGER_ID, { name: 'Trip', color: '#ef4444' }));
    });

    it('asks for a name before it asks the server, and keeps the dialog open on a refusal', async () => {
        const create = vi.spyOn(apiModule, 'createTag').mockRejectedValue(
            new ApiError(422, 'Another tag has that name.', 'tag-name-exists'),
        );
        renderPanel([tag({ id: 't1', name: 'Trip', usageCount: 1, transactionCount: 1 })]);

        await userEvent.click(await screen.findByRole('button', { name: /New tag/ }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));
        expect(await within(dialog).findByText('Name is required.')).toBeInTheDocument();
        expect(create).not.toHaveBeenCalled();

        await userEvent.type(within(dialog).getByLabelText('Name'), 'trip');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));
        expect(await within(dialog).findByText('A tag named “trip” already exists.')).toBeInTheDocument();
        expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('returns to the row’s button when Edit is closed, and starts in the name field', async () => {
        // A real dialog: a field that takes focus itself on mount is focused before Modal records
        // what had it, so closing would leave a keyboard reader on the page body.
        renderPanel([tag({ id: 't1', name: 'work', transactionCount: 3, usageCount: 3 })]);
        const kebab = await screen.findByRole('button', { name: 'Actions for work' });

        await userEvent.click(kebab);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Edit…' }));
        const dialog = await screen.findByRole('dialog');
        expect(within(dialog).getByLabelText('Name')).toHaveFocus();
        await userEvent.keyboard('{Escape}');

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        expect(kebab).toHaveFocus();
    });

    it('returns to the row’s button when the merge a name collision handed over to is closed', async () => {
        // "Merge … instead…" closes the edit dialog and opens the merge dialog in one update.
        vi.spyOn(apiModule, 'patchTag').mockRejectedValue(
            new ApiError(422, 'Another tag has that name.', 'tag-name-exists'),
        );
        renderPanel([
            tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 }),
            tag({ id: 't2', name: 'office', usageCount: 1, transactionCount: 1 }),
        ]);
        const kebab = await screen.findByRole('button', { name: 'Actions for work' });

        await userEvent.click(kebab);
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Edit…' }));
        const edit = await screen.findByRole('dialog', { name: 'Edit tag' });
        await userEvent.clear(within(edit).getByLabelText('Name'));
        await userEvent.type(within(edit).getByLabelText('Name'), 'office');
        await userEvent.click(within(edit).getByRole('button', { name: 'Save' }));
        await userEvent.click(await within(edit).findByRole('button', { name: 'Merge “work” into “office” instead…' }));

        const merge = await screen.findByRole('dialog', { name: 'Merge “work”' });
        expect(merge.contains(document.activeElement)).toBe(true);
        await userEvent.keyboard('{Escape}');

        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
        expect(kebab).toHaveFocus();
    });

    // ----- Delete -----------------------------------------------------------------

    it('deletes a tag via the row menu + confirm', async () => {
        const del = vi.spyOn(apiModule, 'deleteTag').mockResolvedValue(undefined);
        renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);

        // Right-click the row → context menu.
        fireEvent.contextMenu(await screen.findByText('work'));
        fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));

        // The confirmation says how many transactions lose the tag.
        const confirm = await screen.findByRole('dialog');
        expect(within(confirm).getByText('3 transactions')).toBeInTheDocument();
        fireEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));

        await waitFor(() => expect(del).toHaveBeenCalledWith(LEDGER_ID, 't1'));
    });

    // Back to back, before TanStack's notify timeout lets Working… render: the
    // confirm's press holds it until the DELETE it sent has settled (pending.ts).
    it('sends one DELETE for a double press of the confirm', async () => {
        let answer!: () => void;
        const del = vi.spyOn(apiModule, 'deleteTag')
            .mockImplementation(() => new Promise<void>((resolve) => { answer = resolve; }));
        renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);
        fireEvent.contextMenu(await screen.findByText('work'));
        fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
        const confirm = within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' });

        fireEvent.click(confirm);
        fireEvent.click(confirm);
        for (let i = 0; i < 10; i += 1) await Promise.resolve();
        expect(confirm).not.toHaveAttribute('aria-busy');
        expect(del).toHaveBeenCalledTimes(1);

        await waitFor(() => expect(confirm).toHaveAttribute('aria-busy', 'true'));
        fireEvent.click(confirm);
        answer();
        // Settled: the confirm closes, or stands at rest.
        await waitFor(() => expect(!confirm.isConnected || !confirm.hasAttribute('aria-busy')).toBe(true));
        expect(del).toHaveBeenCalledTimes(1);
    });

    it('says a tag no register shows is still on things, and what they are', async () => {
        // Pairings point at it, so it is not unused; no register shows it on a transaction, so
        // it is on none that count. The line names every way that happens, not only one.
        renderPanel([tag({ id: 't1', name: 'old', usageCount: 2, transactionCount: 0 })]);

        fireEvent.contextMenu(await screen.findByText('old'));
        fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));

        const confirm = await screen.findByRole('dialog');
        expect(within(confirm).getByText(/No register shows this tag on a transaction/)).toBeInTheDocument();
        expect(within(confirm).getByText(/hidden or merged-away transactions, on reminders/)).toBeInTheDocument();
        expect(within(confirm).getByText(/behind a line’s own tags/)).toBeInTheDocument();
    });

    // ----- What a write refreshes ---------------------------------------------------

    // The home screen's recent and to-review rows draw each row's tags, so a tag write must
    // refresh its reads (ADR-0079 as amended 2026-11-03), not only the register's.
    const HOME_READS = [['home', LEDGER_ID], ['overview', LEDGER_ID], ['budget-progress', LEDGER_ID]];
    function spyOnInvalidations(queryClient: ReturnType<typeof testQueryClient>) {
        const spy = vi.spyOn(queryClient, 'invalidateQueries');
        return () => spy.mock.calls.map((c) => c[0]?.queryKey);
    }

    it('refreshes the home screen after a rename and recolour', async () => {
        const patch = vi.spyOn(apiModule, 'patchTag').mockResolvedValue(undefined);
        const { queryClient } = renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);
        const keys = spyOnInvalidations(queryClient);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for work' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Edit…' }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.clear(within(dialog).getByLabelText('Name'));
        await userEvent.type(within(dialog).getByLabelText('Name'), 'office');
        await userEvent.click(within(dialog).getByRole('button', { name: 'Colour #ef4444' }));
        // Anchor: nothing is refreshed before the save, so the check after it cannot pass early.
        expect(keys()).toEqual([]);
        await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(patch).toHaveBeenCalledWith(LEDGER_ID, 't1', { name: 'office', color: '#ef4444' }));
        await waitFor(() => expect(keys()).toEqual(expect.arrayContaining(HOME_READS)));
    });

    it('refreshes the home screen after a merge', async () => {
        const merge = vi.spyOn(apiModule, 'mergeTag')
            .mockResolvedValue({ transactionsRepointed: 3, savedReportsRepointed: 0 });
        const { queryClient } = renderPanel([
            tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 }),
            tag({ id: 't2', name: 'office', usageCount: 1, transactionCount: 1 }),
        ]);
        const keys = spyOnInvalidations(queryClient);

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for work' }));
        await userEvent.click(await screen.findByRole('menuitem', { name: 'Merge…' }));
        const dialog = await screen.findByRole('dialog');
        await userEvent.selectOptions(within(dialog).getByLabelText('Merge into'), 't2');
        expect(keys()).toEqual([]);
        await userEvent.click(within(dialog).getByRole('button', { name: /^merge$/i }));

        await waitFor(() => expect(merge).toHaveBeenCalledWith(LEDGER_ID, 't1', { intoTagId: 't2' }));
        await waitFor(() => expect(keys()).toEqual(expect.arrayContaining(HOME_READS)));
    });

    it('refreshes the home screen after a delete', async () => {
        const del = vi.spyOn(apiModule, 'deleteTag').mockResolvedValue(undefined);
        const { queryClient } = renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);
        const keys = spyOnInvalidations(queryClient);

        fireEvent.contextMenu(await screen.findByText('work'));
        fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
        const confirm = await screen.findByRole('dialog');
        expect(keys()).toEqual([]);
        fireEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));

        await waitFor(() => expect(del).toHaveBeenCalledWith(LEDGER_ID, 't1'));
        await waitFor(() => expect(keys()).toEqual(expect.arrayContaining(HOME_READS)));
    });

    it('announces a refusal inside the delete confirm', async () => {
        vi.spyOn(apiModule, 'deleteTag').mockRejectedValue(
            new apiModule.ApiError(422, 'That tag is gone.', 'tag-not-found'),
        );
        renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);

        fireEvent.contextMenu(await screen.findByText('work'));
        fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
        const confirm = await screen.findByRole('dialog');
        fireEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));

        expect(await within(confirm).findByRole('alert')).toHaveTextContent('That tag is gone.');
    });

    // The refusal sits above the confirm's buttons in a centred dialog: cleared
    // at the press of a retry, the dialog would shrink and the buttons move from
    // under the pointer. It stays until the retry's own result (ADR-0023 §L).
    it('keeps a refusal shown in the delete confirm while the retry is in flight', async () => {
        vi.spyOn(apiModule, 'deleteTag')
            .mockRejectedValueOnce(new apiModule.ApiError(422, 'That tag is gone.', 'tag-not-found'))
            .mockImplementationOnce(() => new Promise<void>(() => {}));
        renderPanel([tag({ id: 't1', name: 'work', usageCount: 3, transactionCount: 3 })]);
        fireEvent.contextMenu(await screen.findByText('work'));
        fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
        const confirm = await screen.findByRole('dialog');
        const remove = within(confirm).getByRole('button', { name: 'Delete' });
        fireEvent.click(remove);
        const refusal = await within(confirm).findByRole('alert');

        fireEvent.click(remove);
        await waitFor(() => expect(remove).toHaveAccessibleName('Working…'));
        expect(within(confirm).getByRole('alert')).toBe(refusal);
        expect(refusal).toHaveTextContent('That tag is gone.');
    });

    // ADR-0023 §L: a tag deleted from its own menu takes its row's kebab, the
    // control the confirm gave focus back to, with it. Focus goes to the next
    // row's kebab, else the previous row's, else New tag, never to the page.
    describe('focus after a delete takes the row away', () => {
        const three = [
            tag({ id: 't1', name: 'alpha' }),
            tag({ id: 't2', name: 'beta' }),
            tag({ id: 't3', name: 'gamma' }),
        ];

        async function deleteFromItsMenu(name: string, remaining: TagDto[]) {
            vi.spyOn(apiModule, 'deleteTag').mockResolvedValue(undefined);
            const user = userEvent.setup();
            const kebab = await screen.findByRole('button', { name: `Actions for ${name}` });
            await user.click(kebab);
            vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(remaining);
            await user.click(await screen.findByRole('menuitem', { name: 'Delete' }));
            const confirm = await screen.findByRole('dialog');
            await user.click(within(confirm).getByRole('button', { name: 'Delete' }));
            await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
            await waitFor(() => expect(screen.queryByRole('link', { name })).toBeNull());
        }

        it('goes to the next row\'s actions button', async () => {
            renderPanel(three);
            await deleteFromItsMenu('beta', [three[0]!, three[2]!]);
            expect(screen.getByRole('button', { name: 'Actions for gamma' })).toHaveFocus();
        });

        it('goes to the previous row\'s when the last row goes', async () => {
            renderPanel(three);
            await deleteFromItsMenu('gamma', [three[0]!, three[1]!]);
            expect(screen.getByRole('button', { name: 'Actions for beta' })).toHaveFocus();
        });

        it('goes to New tag when no row is left', async () => {
            renderPanel([three[0]!]);
            await deleteFromItsMenu('alpha', []);
            expect(await screen.findByText('No tags yet')).toBeInTheDocument();
            const [toolbarNew] = screen.getAllByRole('button', { name: /New tag/ });
            expect(toolbarNew).toHaveFocus();
        });
    });
});
