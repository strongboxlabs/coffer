// System → MCP → Connected apps (ADR-0113): the OAuth client registrations, each with the
// live sign-ins made through it, and personal tokens beside them. The rules that turn the two
// admin lists into one live here, apart from the panel, so they can be read and tested whole.

import type { McpClient, McpConnection } from '@/lib/api';

/** How close an expiry has to be before a list flags it. */
export const EXPIRY_WARNING_DAYS = 7;

const DAY_MS = 24 * 60 * 60 * 1000;

/** What an app's single status says about it. */
export type AppStatus =
    /** At least one sign-in has a recorded use; the latest of them. */
    | { kind: 'active'; lastUsedAt: string }
    /** Live sign-ins exist, none with a recorded use (never used, or predating the record). */
    | { kind: 'no-use-recorded' }
    /** No live sign-in: nobody signed in, or every sign-in was revoked or ran past its age. */
    | { kind: 'no-live-sign-in' };

export interface AppGroup {
    client: McpClient;
    /** The app's live sign-ins, most recently used first. */
    signIns: McpConnection[];
    status: AppStatus;
    /**
     * Days until the app's LAST live sign-in expires, when that is within
     * EXPIRY_WARNING_DAYS: the point at which the app loses all access. Null otherwise.
     */
    allExpireInDays: number | null;
}

export interface ConnectedApps {
    apps: AppGroup[];
    /** OAuth sign-ins whose registration is not in the client list (it went mid-read). */
    orphans: McpConnection[];
    /** Personal tokens: pasted into an app, tied to no registration. */
    tokens: McpConnection[];
}

/** Newest first; a missing time sorts after every present one. */
function byTimeDesc(a: string | null, b: string | null): number {
    if (a === b) return 0;
    if (a === null) return 1;
    if (b === null) return -1;
    return b.localeCompare(a);
}

function bySignInOrder(a: McpConnection, b: McpConnection): number {
    return byTimeDesc(a.lastUsedAt, b.lastUsedAt) || byTimeDesc(a.connectedAt, b.connectedAt);
}

/** Whole days until `expiresAt` when within EXPIRY_WARNING_DAYS (0 = today), else null. */
export function expiresWithinDays(expiresAt: string | null, now: number): number | null {
    if (expiresAt === null) return null;
    const ms = Date.parse(expiresAt) - now;
    if (Number.isNaN(ms) || ms > EXPIRY_WARNING_DAYS * DAY_MS) return null;
    return Math.max(0, Math.floor(ms / DAY_MS));
}

function statusOf(signIns: McpConnection[]): AppStatus {
    if (signIns.length === 0) return { kind: 'no-live-sign-in' };
    const used = signIns.map((s) => s.lastUsedAt).filter((t): t is string => t !== null);
    if (used.length === 0) return { kind: 'no-use-recorded' };
    return { kind: 'active', lastUsedAt: used.sort().at(-1)! };
}

const STATUS_RANK: Record<AppStatus['kind'], number> = {
    'active': 0,
    'no-use-recorded': 1,
    'no-live-sign-in': 2,
};

function byAppOrder(a: AppGroup, b: AppGroup): number {
    const rank = STATUS_RANK[a.status.kind] - STATUS_RANK[b.status.kind];
    if (rank !== 0) return rank;
    if (a.status.kind === 'active' && b.status.kind === 'active') {
        const t = byTimeDesc(a.status.lastUsedAt, b.status.lastUsedAt);
        if (t !== 0) return t;
    }
    return (
        byTimeDesc(a.signIns[0]?.connectedAt ?? null, b.signIns[0]?.connectedAt ?? null)
        || byTimeDesc(a.client.registeredAt, b.client.registeredAt)
        || a.client.displayName.localeCompare(b.client.displayName)
        || a.client.clientId.localeCompare(b.client.clientId)
    );
}

/**
 * Group the admin's two lists into one: each registration with the live sign-ins made
 * through it, most recently used app first, apps with no live sign-in last.
 */
export function groupConnectedApps(
    clients: readonly McpClient[],
    connections: readonly McpConnection[],
    now: number,
): ConnectedApps {
    const byClient = new Map<string, McpConnection[]>();
    for (const c of clients) byClient.set(c.clientId, []);

    const orphans: McpConnection[] = [];
    const tokens: McpConnection[] = [];
    for (const c of connections) {
        if (c.kind === 'token') {
            tokens.push(c);
            continue;
        }
        const group = c.clientId === null ? undefined : byClient.get(c.clientId);
        if (group) group.push(c);
        else orphans.push(c);
    }

    const apps = clients.map((client): AppGroup => {
        const signIns = [...byClient.get(client.clientId)!].sort(bySignInOrder);
        const lastExpiry = signIns
            .map((s) => s.expiresAt)
            .filter((t): t is string => t !== null)
            .sort()
            .at(-1) ?? null;
        return {
            client,
            signIns,
            status: statusOf(signIns),
            allExpireInDays: signIns.length > 0 ? expiresWithinDays(lastExpiry, now) : null,
        };
    });

    return {
        apps: apps.sort(byAppOrder),
        orphans: orphans.sort(bySignInOrder),
        tokens: tokens.sort(bySignInOrder),
    };
}

/** Where an app runs, read from where it asks to be sent back after signing in. */
export interface AppIdentity {
    /**
     * `local`: every callback is on the device the person signs in from (a desktop or
     * command-line app). `hosted`: a callback on the internet (a web service's connector).
     */
    kind: 'local' | 'hosted' | 'unknown';
    /** The callback's host (with port for a local one), to tell two of one app apart. */
    host: string | null;
}

const LOOPBACK = new Set(['localhost', '127.0.0.1', '[::1]', '::1']);

export function appIdentity(redirectUris: readonly string[]): AppIdentity {
    const urls = redirectUris.flatMap((raw) => {
        try {
            return [new URL(raw)];
        } catch {
            return [];
        }
    });
    if (urls.length === 0) return { kind: 'unknown', host: null };
    const hosted = urls.find((u) => !LOOPBACK.has(u.hostname));
    if (hosted) return { kind: 'hosted', host: hosted.hostname };
    return { kind: 'local', host: urls[0]!.host };
}

/** The identity in words, for the line under an app's name. */
export function appIdentityText(identity: AppIdentity): string {
    switch (identity.kind) {
        case 'hosted':
            return `Hosted service · ${identity.host}`;
        case 'local':
            return `App on a person’s device · callback on ${identity.host}`;
        case 'unknown':
            return 'No callback address';
    }
}

/** "1 sign-in", "2 sign-ins". */
export function signInCount(n: number): string {
    return `${n} sign-in${n === 1 ? '' : 's'}`;
}

/** "in 1 day", "in 3 days", "today". */
export function inDays(days: number): string {
    if (days === 0) return 'today';
    return `in ${days} day${days === 1 ? '' : 's'}`;
}
