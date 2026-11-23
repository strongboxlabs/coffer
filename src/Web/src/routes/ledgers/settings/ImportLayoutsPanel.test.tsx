import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import * as apiModule from '@/lib/api';
import type { CsvMapping } from '@/lib/types';

import { ImportLayoutsPanel } from './ImportLayoutsPanel';

/**
 * Where saved delimited-file layouts are pruned.
 *
 * Rename and delete are the two things here, and one of them is permanent. These
 * tests pin the clash rule and the delete confirmation, so removing either turns the
 * suite red.
 */
const LAYOUTS: CsvMapping[] = [
    {
        id: 'map-1',
        name: 'Credit card statement',
        definitionYaml: 'version: 1\n# tab-separated, despite the .csv\ndelimiter: tab\n',
        schemaVersion: 1,
        createdAt: '2026-09-01T00:00:00Z',
        updatedAt: '2026-09-02T00:00:00Z',
    },
    {
        id: 'map-2',
        name: 'Checking export',
        definitionYaml: 'version: 1\ndelimiter: comma\n',
        schemaVersion: 1,
        createdAt: '2026-09-01T00:00:00Z',
        updatedAt: '2026-09-03T00:00:00Z',
    },
];

type Ledgers = Awaited<ReturnType<typeof apiModule.fetchVisibleLedgers>>;

function renderPanel(role: 'owner' | 'editor' | 'viewer' = 'owner') {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    // The reader's role, from the read the panel asks it of.
    const ledgers = [{ id: 'led-1', name: 'Ledger', role }] as Ledgers;
    vi.spyOn(apiModule, 'fetchVisibleLedgers').mockResolvedValue(ledgers);
    queryClient.setQueryData(['ledgers'], ledgers);
    render(
        <QueryClientProvider client={queryClient}>
            <ImportLayoutsPanel ledgerId="led-1" />
        </QueryClientProvider>,
    );
    return userEvent.setup();
}

describe('ImportLayoutsPanel', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue(LAYOUTS);
    });

    it('shows each layout and, on request, the document itself', async () => {
        // Verbatim rather than summarised: a summary needs a second YAML reader, and
        // the wizard writes its options out as comments, so the document explains
        // itself to anyone who opens it.
        renderPanel();

        expect(await screen.findByText('Credit card statement')).toBeTruthy();
        expect(screen.getByText('Checking export')).toBeTruthy();
        expect(screen.getByText(/tab-separated, despite the \.csv/)).toBeTruthy();
    });

    it('renames in place, sending the document back untouched', async () => {
        // The update endpoint replaces the whole row, so omitting the document would
        // blank the very thing being renamed.
        const update = vi.spyOn(apiModule, 'updateCsvMapping')
            .mockResolvedValue({ ...LAYOUTS[0]!, name: 'Card layout B' });
        const user = renderPanel();
        await screen.findByText('Credit card statement');

        const row = screen.getByText('Credit card statement').closest('li')!;
        await user.click(within(row).getByRole('button', { name: /rename/i }));

        const box = await screen.findByLabelText(/^name$/i);
        await user.clear(box);
        await user.type(box, 'Card layout B');
        // Scoped: the row that opened this dialog still has its own Rename button.
        await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /^rename$/i }));

        await waitFor(() => expect(update).toHaveBeenCalled());
        expect(update.mock.calls[0]![1]).toBe('map-1');
        expect(update.mock.calls[0]![2]).toBe('Card layout B');
        expect(update.mock.calls[0]![3]).toBe(LAYOUTS[0]!.definitionYaml);
    });

    it('refuses a name another layout already has, but allows its own', async () => {
        // Renaming to the name it already has is a no-op, not a clash — which is why
        // the rule lives with the caller and not inside the shared dialog, where
        // saving a copy needs the opposite answer.
        const user = renderPanel();
        await screen.findByText('Credit card statement');

        const row = screen.getByText('Credit card statement').closest('li')!;
        await user.click(within(row).getByRole('button', { name: /rename/i }));
        const box = await screen.findByLabelText(/^name$/i);

        await user.clear(box);
        await user.type(box, 'Checking export');
        expect(await screen.findByText(/already called that/i)).toBeTruthy();
        expect((within(screen.getByRole('dialog')).getByRole('button', { name: /^rename$/i }) as HTMLButtonElement).disabled)
            .toBe(true);

        await user.clear(box);
        await user.type(box, 'Credit card statement');
        expect(screen.queryByText(/already called that/i)).toBeNull();
        expect((within(screen.getByRole('dialog')).getByRole('button', { name: /^rename$/i }) as HTMLButtonElement).disabled)
            .toBe(false);
    });

    it('confirms before deleting, and says what deleting cannot reach', async () => {
        // Nothing references a layout once an import has run, so a delete cannot touch
        // the transactions it produced. Someone unsure of that keeps dead layouts
        // forever rather than risk their register.
        const remove = vi.spyOn(apiModule, 'deleteCsvMapping').mockResolvedValue(undefined);
        const user = renderPanel();
        await screen.findByText('Credit card statement');

        const row = screen.getByText('Credit card statement').closest('li')!;
        await user.click(within(row).getByRole('button', { name: /delete/i }));

        expect(await screen.findByText(/transactions already imported with it are unaffected/i))
            .toBeTruthy();
        expect(remove).not.toHaveBeenCalled();

        await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /^delete$/i }));

        await waitFor(() => expect(remove).toHaveBeenCalledWith('led-1', 'map-1'));
    });

    it('says the list failed rather than claiming there is nothing saved', async () => {
        vi.spyOn(apiModule, 'fetchCsvMappings').mockRejectedValue(new Error('the server said no'));
        renderPanel();

        const alert = await screen.findByRole('alert');
        expect(alert.textContent).toContain('the server said no');
        expect(screen.queryByText(/none saved yet/i)).toBeNull();
    });

    // ADR-0023 §L: the list has no add control (a layout is saved from an
    // import), so the last delete gives focus to the panel: named, and no Tab
    // stop of its own.
    it('gives focus to the panel once the last deleted layout has gone', async () => {
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue([LAYOUTS[0]!]);
        vi.spyOn(apiModule, 'deleteCsvMapping').mockResolvedValue(undefined);
        const user = renderPanel();
        const row = (await screen.findByText('Credit card statement')).closest('li')!;

        await user.click(within(row).getByRole('button', { name: /delete/i }));
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue([]);
        await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /^delete$/i }));

        expect(await screen.findByText(/none saved yet/i)).toBeInTheDocument();
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        const panel = screen.getByRole('group', { name: 'Import layouts' });
        expect(panel).toHaveAttribute('tabindex', '-1');
        await waitFor(() => expect(panel).toHaveFocus());
    });
});

describe('ImportLayoutsPanel — by role (ADR-0023 §L)', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue(LAYOUTS);
    });

    it('gives a viewer the layouts and their documents, and no rename or delete', async () => {
        renderPanel('viewer');
        const row = (await screen.findByText('Credit card statement')).closest('li') as HTMLElement;
        expect(within(row).getByText('Show document')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Rename' })).toBeNull();
        expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    });

    it('tells a viewer with none saved nothing to import', async () => {
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue([]);
        renderPanel('viewer');
        expect(await screen.findByText('None saved yet.')).toBeInTheDocument();
    });

    it.each(['owner', 'editor'] as const)('keeps rename and delete for an %s', async (role) => {
        renderPanel(role);
        const row = (await screen.findByText('Credit card statement')).closest('li') as HTMLElement;
        expect(within(row).getAllByRole('button').map((b) => b.textContent)).toEqual(['Rename', 'Delete']);
    });
});
