// MCP access-token management (ADR-0063), the "Connected apps" surface.
// Account-domain endpoints per the ADR-0030 lib/api partition.

import { request } from './_request';

/** Mirror of API `McpTokenSummary` — metadata only, never the plaintext. */
export interface McpTokenSummary {
    id: string;
    name: string;
    scopes: string;
    createdAt: string;
    lastUsedAt: string | null;
    expiresAt: string | null;
}

/**
 * Mirror of API `IssuedMcpToken`. `token` is the plaintext, returned exactly
 * once at creation — the caller MUST show it to the user immediately; it is
 * never retrievable again (only its SHA-256 is stored).
 */
export interface IssuedMcpToken {
    id: string;
    name: string;
    scopes: string;
    expiresAt: string | null;
    token: string;
}

/**
 * List the current user's active MCP tokens. When MCP is disabled for the
 * deployment the endpoint isn't mapped and this rejects with a 404 `ApiError` —
 * the page renders that as the "turned off" state rather than an error.
 */
export function fetchMcpTokens(): Promise<McpTokenSummary[]> {
    return request<McpTokenSummary[]>('/api/account/mcp-tokens', { method: 'GET' });
}

/** Mint a token for a connected app. Returns the one-time plaintext. */
export function createMcpToken(name: string): Promise<IssuedMcpToken> {
    return request<IssuedMcpToken>('/api/account/mcp-tokens', {
        method: 'POST',
        body: { name },
    });
}

/** Revoke one of the current user's tokens. */
export function revokeMcpToken(id: string): Promise<void> {
    return request<void>(`/api/account/mcp-tokens/${encodeURIComponent(id)}`, {
        method: 'DELETE',
    });
}

/**
 * Mirror of API `McpSettingResponse` — the admin MCP runtime toggle (ADR-0063 §D8).
 * `enabled` is the persisted desired state; `active` is whether MCP is live in the
 * running server (differs → pending restart); `configForced` means env config
 * forces it on regardless of this setting.
 */
export interface McpSetting {
    enabled: boolean;
    active: boolean;
    configForced: boolean;
    /** ADR-0068: the MCP write-tools toggle. Only meaningful when `enabled`. */
    writesEnabled: boolean;
    writesActive: boolean;
    writesConfigForced: boolean;
    /**
     * Address to give an MCP client. `Api:Mcp:PublicUrl` (`COFFER_MCP_URL`) when
     * configured, else the origin the request was served on — which is right for
     * a single-host install and wrong for one whose MCP server answers on its own
     * hostname, so the configured value wins.
     */
    publicUrl: string;
}

/** Read the MCP runtime toggle (admin only). */
export function fetchMcpSetting(): Promise<McpSetting> {
    return request<McpSetting>('/api/admin/system-settings/mcp', { method: 'GET' });
}

/** Set the MCP runtime toggles (admin only). `enabled` (the master switch) takes
 *  effect on the next API restart; `writesEnabled` (ADR-0068/0081 D2) is a HOT
 *  flag — the write kill-switch flips immediately, no restart. */
export function setMcpSetting(enabled: boolean, writesEnabled: boolean): Promise<McpSetting> {
    return request<McpSetting>('/api/admin/system-settings/mcp', {
        method: 'PUT',
        body: { enabled, writesEnabled },
    });
}

// --- Admin: OAuth client management (ADR-0081 D5) ---

/** Mirror of API `McpClientDto` — an OAuth client registered against the MCP AS. */
export interface McpClient {
    clientId: string;
    /**
     * The name the client registered itself under via DCR. Client-supplied, so
     * every install of a given client reports the same string — two laptops
     * running Claude are two rows both called "Claude".
     */
    displayName: string;
    clientType: string;
    redirectUris: string[];
    activeAuthorizations: number;
    /** Operator-assigned name; shown in preference to `displayName` when set. */
    label: string | null;
}

/** List the OAuth clients that can reach `/mcp` (admin only). 404 when MCP is off. */
export function fetchMcpClients(): Promise<McpClient[]> {
    return request<McpClient[]>('/api/admin/mcp/clients', { method: 'GET' });
}

/**
 * Rename a client. Pass null or an empty string to clear the label and fall back
 * to the client's own registered name. The label lives with the registration, so
 * revoking and re-registering the client loses it.
 */
export function setMcpClientLabel(clientId: string, label: string | null): Promise<void> {
    return request<void>(`/api/admin/mcp/clients/${encodeURIComponent(clientId)}`, {
        method: 'PATCH',
        body: JSON.stringify({ label }),
    });
}

/** Revoke a client — deletes it and its tokens + authorizations (admin only). */
export function revokeMcpClient(clientId: string): Promise<void> {
    return request<void>(`/api/admin/mcp/clients/${encodeURIComponent(clientId)}`, {
        method: 'DELETE',
    });
}

/** Prune clients with no authorizations (admin only); returns how many were removed. */
export function pruneMcpClients(): Promise<{ pruned: number }> {
    return request<{ pruned: number }>('/api/admin/mcp/clients/prune', { method: 'POST' });
}

// --- Admin: MCP write audit (ADR-0081 D3) ---

/** Lifecycle state of an MCP write-tool invocation (ADR-0086). */
export type McpInvocationStatus = 'pending' | 'ok' | 'error' | 'cancelled';

/** Mirror of API `McpAuditEntryDto` — one recorded MCP write-tool invocation. */
export interface McpAuditEntry {
    id: string;
    userId: string;
    user: string;
    toolName: string;
    ledgerId: string | null;
    arguments: string | null;
    status: McpInvocationStatus;
    result: string | null;
    createdAt: string;
    completedAt: string | null;
}

/** Read the MCP write audit, newest first (admin only). 404 when MCP is off. */
export function fetchMcpAudit(take = 100): Promise<McpAuditEntry[]> {
    return request<McpAuditEntry[]>(`/api/admin/mcp/audit?take=${take}`, { method: 'GET' });
}

/** Clear the MCP write audit (admin only); returns how many rows were removed. */
export function clearMcpAudit(): Promise<{ deleted: number }> {
    return request<{ deleted: number }>('/api/admin/mcp/audit', { method: 'DELETE' });
}
