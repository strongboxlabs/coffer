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
 * Written after a mutation went green: the panel shipped with no tests at all, so
 * deleting its clash rule — or its delete confirmation — changed nothing the suite
 * could see. Rename and delete are the two things here, and one of them is permanent.
 */
const LAYOUTS: CsvMapping[] = [
    {
        id: 'map-1',
        name: 'Store card statement',
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

function renderPanel() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
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

        expect(await screen.findByText('Store card statement')).toBeTruthy();
        expect(screen.getByText('Checking export')).toBeTruthy();
        expect(screen.getByText(/tab-separated, despite the \.csv/)).toBeTruthy();
    });

    it('renames in place, sending the document back untouched', async () => {
        // The update endpoint replaces the whole row, so omitting the document would
        // blank the very thing being renamed.
        const update = vi.spyOn(apiModule, 'updateCsvMapping')
            .mockResolvedValue({ ...LAYOUTS[0]!, name: 'Macy card' });
        const user = renderPanel();
        await screen.findByText('Store card statement');

        const row = screen.getByText('Store card statement').closest('li')!;
        await user.click(within(row).getByRole('button', { name: /rename/i }));

        const box = await screen.findByLabelText(/^name$/i);
        await user.clear(box);
        await user.type(box, 'Macy card');
        // Scoped: the row that opened this dialog still has its own Rename button.
        await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /^rename$/i }));

        await waitFor(() => expect(update).toHaveBeenCalled());
        expect(update.mock.calls[0]![1]).toBe('map-1');
        expect(update.mock.calls[0]![2]).toBe('Macy card');
        expect(update.mock.calls[0]![3]).toBe(LAYOUTS[0]!.definitionYaml);
    });

    it('refuses a name another layout already has, but allows its own', async () => {
        // Renaming to the name it already has is a no-op, not a clash — which is why
        // the rule lives with the caller and not inside the shared dialog, where
        // saving a copy needs the opposite answer.
        const user = renderPanel();
        await screen.findByText('Store card statement');

        const row = screen.getByText('Store card statement').closest('li')!;
        await user.click(within(row).getByRole('button', { name: /rename/i }));
        const box = await screen.findByLabelText(/^name$/i);

        await user.clear(box);
        await user.type(box, 'Checking export');
        expect(await screen.findByText(/already called that/i)).toBeTruthy();
        expect((within(screen.getByRole('dialog')).getByRole('button', { name: /^rename$/i }) as HTMLButtonElement).disabled)
            .toBe(true);

        await user.clear(box);
        await user.type(box, 'Store card statement');
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
        await screen.findByText('Store card statement');

        const row = screen.getByText('Store card statement').closest('li')!;
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
});
