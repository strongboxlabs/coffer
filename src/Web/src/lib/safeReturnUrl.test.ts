import { describe, expect, it } from 'vitest';

import { safeReturnUrl } from './safeReturnUrl';

const origin = 'https://coffer.example.com';

describe('safeReturnUrl', () => {
    it('follows the authorization endpoint on this origin, query and all', () => {
        expect(safeReturnUrl('/oauth/authorize?client_id=a&state=s', origin))
            .toBe('/oauth/authorize?client_id=a&state=s');
    });

    it.each([
        // The audit's S6: the tab is dropped when the browser parses the URL.
        '/%09/evil.com',
        '/\t/evil.com',
        '/\n/evil.com',
        '//evil.com',
        '/\\evil.com',
        'https://evil.com/oauth/authorize',
        'javascript:alert(1)',
        // Same origin, but not where sign-in is ever sent back to.
        '/api/admin/users',
        '/',
    ])('refuses %j', (raw) => {
        expect(safeReturnUrl(raw, origin)).toBeNull();
    });
});
