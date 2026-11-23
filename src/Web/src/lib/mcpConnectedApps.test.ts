import { describe, it, expect } from 'vitest';

import type { McpClient, McpConnection } from '@/lib/api';
import {
    appIdentity,
    expiresWithinDays,
    groupConnectedApps,
    EXPIRY_WARNING_DAYS,
} from './mcpConnectedApps';

const NOW = Date.parse('2026-11-16T12:00:00Z');
const DAY = 24 * 60 * 60 * 1000;

const client = (id: string, over: Partial<McpClient> = {}): McpClient => ({
    clientId: id, displayName: id, clientType: 'public', redirectUris: [],
    liveConnections: 0, registeredAt: null, ...over,
});
const signIn = (id: string, clientId: string | null, over: Partial<McpConnection> = {}): McpConnection => ({
    kind: 'oauth', id, userId: 'u', user: 'User', name: clientId ?? 'x', label: null,
    appName: clientId, clientId, canWrite: true, connectedAt: '2026-10-01T00:00:00Z',
    lastUsedAt: null, expiresAt: '2027-01-01T00:00:00Z', ...over,
});

describe('groupConnectedApps', () => {
    it('groups by client id, keeps tokens apart, and holds a sign-in with no listed app as an orphan', () => {
        const token: McpConnection = { ...signIn('t', null), kind: 'token' };
        const g = groupConnectedApps(
            [client('a'), client('b')],
            [signIn('1', 'a'), signIn('2', 'b'), signIn('3', 'gone'), signIn('4', null), token],
            NOW,
        );

        expect(g.apps.find((x) => x.client.clientId === 'a')!.signIns.map((s) => s.id)).toEqual(['1']);
        expect(g.apps.find((x) => x.client.clientId === 'b')!.signIns.map((s) => s.id)).toEqual(['2']);
        expect(g.orphans.map((s) => s.id).sort()).toEqual(['3', '4']);
        expect(g.tokens.map((s) => s.id)).toEqual(['t']);
    });

    it('orders apps: used (latest first), then live but unrecorded, then no live sign-in', () => {
        const g = groupConnectedApps(
            [client('none'), client('unrecorded'), client('older'), client('newer')],
            [
                signIn('1', 'unrecorded'),
                signIn('2', 'older', { lastUsedAt: '2026-11-01T00:00:00Z' }),
                signIn('3', 'newer', { lastUsedAt: '2026-11-15T00:00:00Z' }),
            ],
            NOW,
        );

        expect(g.apps.map((a) => a.client.clientId)).toEqual(['newer', 'older', 'unrecorded', 'none']);
        expect(g.apps.map((a) => a.status.kind)).toEqual(['active', 'active', 'no-use-recorded', 'no-live-sign-in']);
    });

    it('takes an app’s last use from its most recently used sign-in, which it lists first', () => {
        const g = groupConnectedApps(
            [client('a')],
            [
                signIn('old', 'a', { lastUsedAt: '2026-11-01T00:00:00Z' }),
                signIn('idle', 'a'),
                signIn('new', 'a', { lastUsedAt: '2026-11-10T00:00:00Z' }),
            ],
            NOW,
        );

        const app = g.apps[0]!;
        expect(app.status).toEqual({ kind: 'active', lastUsedAt: '2026-11-10T00:00:00Z' });
        expect(app.signIns.map((s) => s.id)).toEqual(['new', 'old', 'idle']);
    });

    it('warns that an app loses its access only when its LAST sign-in expires soon', () => {
        const soon = new Date(NOW + 2 * DAY + 1000).toISOString();
        const later = new Date(NOW + 60 * DAY).toISOString();
        const g = groupConnectedApps(
            [client('mixed'), client('all-soon')],
            [
                signIn('1', 'mixed', { expiresAt: soon }),
                signIn('2', 'mixed', { expiresAt: later }),
                signIn('3', 'all-soon', { expiresAt: soon }),
            ],
            NOW,
        );

        expect(g.apps.find((a) => a.client.clientId === 'mixed')!.allExpireInDays).toBeNull();
        expect(g.apps.find((a) => a.client.clientId === 'all-soon')!.allExpireInDays).toBe(2);
    });
});

describe('expiresWithinDays', () => {
    it('is the whole days left inside the warning window, and null outside it', () => {
        expect(expiresWithinDays(new Date(NOW + 3 * DAY + 1000).toISOString(), NOW)).toBe(3);
        expect(expiresWithinDays(new Date(NOW + 1000).toISOString(), NOW)).toBe(0);
        expect(expiresWithinDays(new Date(NOW + EXPIRY_WARNING_DAYS * DAY + 1000).toISOString(), NOW)).toBeNull();
        expect(expiresWithinDays(null, NOW)).toBeNull();
    });
});

describe('appIdentity', () => {
    it('calls an app with only loopback callbacks local, keeping the port to tell installs apart', () => {
        expect(appIdentity(['http://localhost:3118/callback'])).toEqual({ kind: 'local', host: 'localhost:3118' });
        expect(appIdentity(['http://127.0.0.1:5525/cb'])).toEqual({ kind: 'local', host: '127.0.0.1:5525' });
        expect(appIdentity(['http://[::1]:9000/cb'])).toEqual({ kind: 'local', host: '[::1]:9000' });
    });

    it('calls an app with any internet callback hosted, naming that host', () => {
        expect(appIdentity(['http://localhost:1/cb', 'https://connector.example.com/r/abc']))
            .toEqual({ kind: 'hosted', host: 'connector.example.com' });
    });

    it('says unknown, not local, when there is no readable callback', () => {
        expect(appIdentity([])).toEqual({ kind: 'unknown', host: null });
        expect(appIdentity(['not a url'])).toEqual({ kind: 'unknown', host: null });
    });
});
