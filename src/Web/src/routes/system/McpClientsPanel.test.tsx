import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { McpClientsPanel } from './McpClientsPanel';
import * as apiModule from '@/lib/api';

// Client labels. The problem they solve: DCR display names are client-supplied, so
// every install of a given client registers under the same string — two laptops
// running Claude produce two identical rows, and revoking the wrong one is
// indistinguishable from revoking the right one until something stops working.

const CLIENT = {
    clientId: 'abc123',
    displayName: 'Claude',
    clientType: 'public',
    redirectUris: ['https://claude.ai/callback'],
    activeAuthorizations: 1,
    label: null as string | null,
};

function renderPanel() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={qc}>
            <McpClientsPanel />
        </QueryClientProvider>,
    );
}

describe('McpClientsPanel — labels', () => {
    beforeEach(() => vi.restoreAllMocks());

    it('falls back to the registered name when unlabelled', async () => {
        vi.spyOn(apiModule, 'fetchMcpClients').mockResolvedValue([CLIENT]);
        renderPanel();

        expect(await screen.findByText('Claude')).toBeInTheDocument();
    });

    it('shows the label but keeps the registered name visible', async () => {
        // Both matter: the label says WHICH connection, the registered name says
        // which software. Replacing one with the other loses information the
        // operator needs when deciding what to revoke.
        vi.spyOn(apiModule, 'fetchMcpClients')
            .mockResolvedValue([{ ...CLIENT, label: 'Laptop' }]);
        renderPanel();

        expect(await screen.findByText('Laptop')).toBeInTheDocument();
        expect(screen.getByText('(Claude)')).toBeInTheDocument();
    });

    it('sends the typed label', async () => {
        vi.spyOn(apiModule, 'fetchMcpClients').mockResolvedValue([CLIENT]);
        const save = vi.spyOn(apiModule, 'setMcpClientLabel').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.type(screen.getByLabelText(/name for claude/i), 'Laptop');
        await user.click(screen.getByRole('button', { name: /^save$/i }));

        expect(save).toHaveBeenCalledWith('abc123', 'Laptop');
    });

    it('clears the label when saved blank rather than storing an empty name', async () => {
        // Otherwise the row renders an empty string as its name, which reads as a
        // broken client rather than an unlabelled one.
        vi.spyOn(apiModule, 'fetchMcpClients')
            .mockResolvedValue([{ ...CLIENT, label: 'Laptop' }]);
        const save = vi.spyOn(apiModule, 'setMcpClientLabel').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.clear(screen.getByLabelText(/name for claude/i));
        await user.click(screen.getByRole('button', { name: /^save$/i }));

        expect(save).toHaveBeenCalledWith('abc123', null);
    });

    it('does not save on cancel', async () => {
        vi.spyOn(apiModule, 'fetchMcpClients').mockResolvedValue([CLIENT]);
        const save = vi.spyOn(apiModule, 'setMcpClientLabel').mockResolvedValue(undefined);
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: /rename/i }));
        await user.type(screen.getByLabelText(/name for claude/i), 'Discarded');
        await user.click(screen.getByRole('button', { name: /^cancel$/i }));

        expect(save).not.toHaveBeenCalled();
        expect(screen.getByText('Claude')).toBeInTheDocument();
    });
});
