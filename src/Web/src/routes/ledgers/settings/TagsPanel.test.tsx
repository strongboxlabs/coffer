import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { TagsPanel } from './TagsPanel';
import * as apiModule from '@/lib/api';
import type { TagDto } from '@/lib/types';

// Smoke tests for the Tags management panel:
//   * Lists tags with usage counts; empty state.
//   * "Remove N unused" appears only with orphans and calls cleanup.
//   * Right-click a row → Delete → confirm → calls deleteTag.
//   * A VISIBLE per-row actions button opens the same menu — the panel had no
//     visible controls at all, so every action was right-click-only.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

function renderPanel(tags: TagDto[]) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    queryClient.setQueryData(['tags', LEDGER_ID], tags);
    return render(
        <QueryClientProvider client={queryClient}>
            <TagsPanel ledgerId={LEDGER_ID} />
        </QueryClientProvider>,
    );
}

describe('TagsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('lists tags with usage counts', async () => {
        const tags: TagDto[] = [
            { id: 't1', name: 'work', color: '#3b82f6', usageCount: 3 },
            { id: 't2', name: 'home', color: null, usageCount: 0 },
        ];
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(tags);
        renderPanel(tags);

        expect(await screen.findByText('work')).toBeInTheDocument();
        expect(screen.getByText('home')).toBeInTheDocument();
        expect(screen.getByText('3 txns')).toBeInTheDocument();
    });

    it('renders the empty state when there are no tags', async () => {
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue([]);
        renderPanel([]);
        expect(await screen.findByText(/No tags yet/i)).toBeInTheDocument();
    });

    it('shows "Remove unused" only with orphans and calls cleanup', async () => {
        const tags: TagDto[] = [
            { id: 't1', name: 'work', color: null, usageCount: 3 },
            { id: 't2', name: 'stale', color: null, usageCount: 0 },
        ];
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(tags);
        const cleanup = vi
            .spyOn(apiModule, 'cleanupUnusedTags')
            .mockResolvedValue({ tagsRemoved: 1 });
        renderPanel(tags);

        const btn = await screen.findByRole('button', { name: /Remove 1 unused/i });
        fireEvent.click(btn);
        await waitFor(() => expect(cleanup).toHaveBeenCalledWith(LEDGER_ID));
    });

    it('hides "Remove unused" when every tag is in use', async () => {
        const tags: TagDto[] = [{ id: 't1', name: 'work', color: null, usageCount: 3 }];
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(tags);
        renderPanel(tags);

        await screen.findByText('work');
        expect(screen.queryByRole('button', { name: /Remove .* unused/i })).not.toBeInTheDocument();
    });

    it('gives every row a visible actions button', async () => {
        // The defect this guards: the panel rendered zero Buttons and zero
        // IconButtons, so rename/recolour/merge/delete were reachable only by
        // right-clicking, advertised by a title tooltip that does not exist on
        // touch and is not announced as an affordance.
        renderPanel([
            { id: 't1', name: 'work', color: '#3b82f6', usageCount: 3 },
            { id: 't2', name: 'home', color: null, usageCount: 1 },
        ]);
        expect(await screen.findByRole('button', { name: 'Actions for work' }))
            .toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Actions for home' }))
            .toBeInTheDocument();
    });

    it('opens the same menu from the button as from right-click', async () => {
        renderPanel([{ id: 't1', name: 'work', color: '#3b82f6', usageCount: 3 }]);

        // Anchor: the menu is absent before the click, so the assertion after
        // it cannot pass on a menu that was always there.
        expect(screen.queryByText('Rename / recolour…')).not.toBeInTheDocument();

        fireEvent.click(await screen.findByRole('button', { name: 'Actions for work' }));

        expect(await screen.findByText('Rename / recolour…')).toBeInTheDocument();
        expect(screen.getByText('Merge…')).toBeInTheDocument();
        expect(screen.getByText('Delete')).toBeInTheDocument();
    });

    it('deletes a tag via the row menu + confirm', async () => {
        const tags: TagDto[] = [{ id: 't1', name: 'work', color: null, usageCount: 3 }];
        vi.spyOn(apiModule, 'fetchTags').mockResolvedValue(tags);
        const del = vi.spyOn(apiModule, 'deleteTag').mockResolvedValue(undefined);
        renderPanel(tags);

        // Right-click the row → context menu.
        fireEvent.contextMenu(await screen.findByText('work'));
        fireEvent.click(screen.getByText('Delete'));

        // Confirm dialog → confirm.
        const confirm = await screen.findByRole('button', { name: 'Delete' });
        fireEvent.click(confirm);

        await waitFor(() => expect(del).toHaveBeenCalledWith(LEDGER_ID, 't1'));
    });
});
