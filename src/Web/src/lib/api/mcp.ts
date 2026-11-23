// MCP access-token management (ADR-0063), the "Connected apps" surface.
// Account-domain endpoints per the ADR-0030 lib/api partition.

import { request } from './_request';
import { stepUpHeaders, withStepUpIfRequired } from './stepUp';

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

/**
 * Mint a token for a connected app. Returns the one-time plaintext. A token reads
 * everything the user can for up to 90 days, so it needs a passkey tap (ADR-0105 D1).
 */
export async function createMcpToken(name: string): Promise<IssuedMcpToken> {
    const headers = await stepUpHeaders('mcp-token');
    return request<IssuedMcpToken>('/api/account/mcp-tokens', {
        method: 'POST',
        body: { name },
        headers,
    });
}

/** Rename one of the current user's tokens. A token keeps a name: blank is refused. */
export function renameMcpToken(id: string, name: string): Promise<void> {
    return request<void>(`/api/account/mcp-tokens/${encodeURIComponent(id)}`, {
        method: 'PATCH',
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
 * Mirror of API `OAuthGrantSummary`: an app the user connected by signing in through
 * OAuth (FU-087), as opposed to pasting a token. It lasts 90 days from the connection.
 */
export interface OAuthGrantSummary {
    id: string;
    /**
     * The name the app registered itself under. Every install of one app reports the same
     * string, so this alone cannot tell two of them apart: that is what `label` is for.
     */
    appName: string;
    canWrite: boolean;
    connectedAt: string;
    expiresAt: string;
    /** The user's own name for this connection; null when they have not set one. */
    label: string | null;
    /** Null when no use is recorded: never used, or connected before use was recorded. */
    lastUsedAt: string | null;
}

/** The apps the current user connected through OAuth. 404 when MCP is off, like the tokens. */
export function fetchOAuthGrants(): Promise<OAuthGrantSummary[]> {
    return request<OAuthGrantSummary[]>('/api/account/oauth-grants', { method: 'GET' });
}

/** Name one of the current user's OAuth connections. A blank name clears it. */
export function renameOAuthGrant(id: string, name: string): Promise<void> {
    return request<void>(`/api/account/oauth-grants/${encodeURIComponent(id)}`, {
        method: 'PATCH',
        body: { name },
    });
}

/** Revoke one: the app's grant and every token issued under it end. */
export function revokeOAuthGrant(id: string): Promise<void> {
    return request<void>(`/api/account/oauth-grants/${encodeURIComponent(id)}`, {
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
 *  flag — the write kill-switch flips immediately, no restart.
 *  Turning writes ON needs a passkey tap (ADR-0105 D1); turning them off
 *  never does — the kill-switch stays one click. */
export function setMcpSetting(enabled: boolean, writesEnabled: boolean): Promise<McpSetting> {
    return withStepUpIfRequired('mcp-writes', (headers) =>
        request<McpSetting>('/api/admin/system-settings/mcp', {
            method: 'PUT',
            body: { enabled, writesEnabled },
            headers,
        }),
    );
}

// --- Admin: connections to /mcp and the OAuth clients behind them (ADR-0081 D5) ---

/** What a connection is: a personal token, or an app the owner signed in to. */
export type McpConnectionKind = 'token' | 'oauth';

/** Mirror of API `McpConnectionDto` — one connection to `/mcp`, with who holds it. */
export interface McpConnection {
    kind: McpConnectionKind;
    /** A token's id or an OAuth authorization's id; with `kind` it addresses the connection. */
    id: string;
    userId: string;
    /** The owner's display name. */
    user: string;
    /** What to call it: the owner's name for it, else the app's own. */
    name: string;
    /** The owner's name for an OAuth connection; null when unset, and always null for a token. */
    label: string | null;
    /** The app's own registered name; OAuth only. Says which software this is. */
    appName: string | null;
    /**
     * The client id of the registration an OAuth connection was made through, matching
     * `McpClient.clientId`; null for a token, and for one whose registration is gone.
     */
    clientId: string | null;
    canWrite: boolean;
    connectedAt: string;
    /** Null when no use is recorded: never used, or connected before use was recorded. */
    lastUsedAt: string | null;
    expiresAt: string | null;
}

/** Every live connection of every user (admin only). 404 when MCP is off. */
export function fetchMcpConnections(): Promise<McpConnection[]> {
    return request<McpConnection[]>('/api/admin/mcp/connections', { method: 'GET' });
}

/**
 * Rename anyone's connection. A blank name clears an OAuth connection's name, falling
 * back to the app's own; a token must keep one.
 */
export function renameMcpConnection(
    kind: McpConnectionKind,
    id: string,
    name: string,
): Promise<void> {
    return request<void>(
        `/api/admin/mcp/connections/${kind}/${encodeURIComponent(id)}`,
        { method: 'PATCH', body: { name } },
    );
}

/** Revoke anyone's connection (admin only); its tokens stop working immediately. */
export function revokeMcpConnection(kind: McpConnectionKind, id: string): Promise<void> {
    return request<void>(`/api/admin/mcp/connections/${kind}/${encodeURIComponent(id)}`, {
        method: 'DELETE',
    });
}

/**
 * Mirror of API `McpClientDto` — an OAuth client registered against the MCP AS. A
 * registration is shared by everyone who connects through it, so it says nothing about
 * who is using it: that is `McpConnection`.
 */
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
    /**
     * How many live connections (valid, inside their age) were made through it: what
     * revoking it ends, by the rule the connection list shows by. Zero means prune removes it.
     */
    liveConnections: number;
    /** When it registered; null for one registered before Coffer recorded it. */
    registeredAt: string | null;
}

/** List the OAuth clients that can reach `/mcp` (admin only). 404 when MCP is off. */
export function fetchMcpClients(): Promise<McpClient[]> {
    return request<McpClient[]>('/api/admin/mcp/clients', { method: 'GET' });
}

/** Revoke a client — deletes it and its tokens + authorizations (admin only). */
export function revokeMcpClient(clientId: string): Promise<void> {
    return request<void>(`/api/admin/mcp/clients/${encodeURIComponent(clientId)}`, {
        method: 'DELETE',
    });
}

/** Prune clients with no live connection (admin only); returns how many were removed. */
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

/**
 * What the OAuth consent page shows (ADR-0105 D5): the client and redirect as
 * the SERVER registered them, looked up by the consent ticket — not the name a link
 * carries. 404 once the ticket has expired, been used, or belongs to someone else.
 */
export interface OAuthConsentInfo {
    clientId: string;
    /** The name the app gave itself when it registered; null when it gave none. */
    clientName: string | null;
    redirectUri: string;
    redirectHost: string;
    scopes: string[];
}

export function fetchOAuthConsentInfo(ticket: string): Promise<OAuthConsentInfo> {
    return request<OAuthConsentInfo>(`/api/oauth/consent/${encodeURIComponent(ticket)}`, {
        method: 'GET',
    });
}
