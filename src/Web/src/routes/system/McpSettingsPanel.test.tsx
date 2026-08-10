import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { McpSettingsPanel } from './McpSettingsPanel';
import * as apiModule from '@/lib/api';

// The connect-address block. Everything else on this panel is a toggle already
// covered from SystemSettingsPage; what's worth pinning here is WHEN an address
// is shown, because both failure directions mislead an operator: showing an
// address for a server that isn't answering sends them debugging the client, and
// hiding one they need sends them to the docs.

const BASE = {
    enabled: true,
    active: true,
    configForced: false,
    writesEnabled: false,
    writesActive: false,
    writesConfigForced: false,
    publicUrl: 'https://mcp.example.test',
};

function renderPanel() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <McpSettingsPanel />
        </QueryClientProvider>,
    );
}

describe('McpSettingsPanel — connect address', () => {
    beforeEach(() => vi.restoreAllMocks());

    it('shows the configured address with the /mcp path appended', async () => {
        // The panel shows a full endpoint, not a bare origin: a client needs the
        // path, and making the operator remember to append it is a support ticket.
        vi.spyOn(apiModule, 'fetchMcpSetting').mockResolvedValue(BASE);
        renderPanel();

        expect(await screen.findByText('https://mcp.example.test/mcp')).toBeInTheDocument();
    });

    it('uses the configured host even though the UI is served from another one', async () => {
        // The split-host case, and the reason PublicUrl exists at all. jsdom serves
        // this page from localhost; a request-derived address would say localhost,
        // which is exactly the wrong answer for an install whose MCP server answers
        // on its own hostname.
        vi.spyOn(apiModule, 'fetchMcpSetting').mockResolvedValue(BASE);
        renderPanel();

        const shown = await screen.findByText(/\/mcp$/);
        expect(shown.textContent).toContain('mcp.example.test');
        expect(shown.textContent).not.toContain('localhost');
    });

    it('shows nothing while MCP is stopped', async () => {
        // Enabled but not yet restarted. An address here points at a server that
        // will refuse the connection.
        vi.spyOn(apiModule, 'fetchMcpSetting').mockResolvedValue({ ...BASE, active: false });
        renderPanel();

        await screen.findByText(/MCP is stopped/i);
        expect(screen.queryByText(/\/mcp$/)).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /^copy$/i })).not.toBeInTheDocument();
    });

    it('shows nothing when no address could be determined', async () => {
        vi.spyOn(apiModule, 'fetchMcpSetting').mockResolvedValue({ ...BASE, publicUrl: '' });
        renderPanel();

        await screen.findByText(/MCP is running/i);
        expect(screen.queryByRole('button', { name: /^copy$/i })).not.toBeInTheDocument();
    });
});
