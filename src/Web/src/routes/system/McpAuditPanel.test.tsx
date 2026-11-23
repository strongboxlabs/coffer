import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { McpAuditPanel } from './McpAuditPanel';
import * as apiModule from '@/lib/api';
import type { McpAuditEntry } from '@/lib/api';

// The MCP write log. Clear log empties it, and goes with the rows: the list has
// no add control, so focus goes to the list itself (ADR-0023 §L).

const ENTRY: McpAuditEntry = {
    id: 'a-1', userId: 'u-1', user: 'Example User', toolName: 'create_transaction',
    ledgerId: null, arguments: null, status: 'ok', result: null,
    createdAt: '2026-11-01T10:00:00Z', completedAt: '2026-11-01T10:00:01Z',
};

function renderPanel() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={qc}>
            <McpAuditPanel />
        </QueryClientProvider>,
    );
}

describe('McpAuditPanel', () => {
    beforeEach(() => vi.restoreAllMocks());

    it('gives focus to the list once Clear log has emptied it and gone with the rows', async () => {
        vi.spyOn(apiModule, 'fetchMcpAudit').mockResolvedValue([ENTRY]);
        vi.spyOn(apiModule, 'clearMcpAudit').mockResolvedValue({ deleted: 1 });
        const user = userEvent.setup();
        renderPanel();

        await user.click(await screen.findByRole('button', { name: 'Clear log' }));
        vi.spyOn(apiModule, 'fetchMcpAudit').mockResolvedValue([]);
        await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Clear log' }));

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        expect(screen.getByText('No AI writes recorded yet.')).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Clear log' })).toBeNull();
        const list = screen.getByRole('group', { name: 'AI write activity' });
        expect(list).toHaveAttribute('tabindex', '-1');
        expect(list).toHaveFocus();
    });
});
