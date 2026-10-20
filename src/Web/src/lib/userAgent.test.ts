import { describe, expect, it } from 'vitest';

import { describeUserAgent } from './userAgent';

describe('describeUserAgent', () => {
    it.each([
        [
            'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0',
            'Edge on Windows',
        ],
        [
            'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36',
            'Chrome on Windows',
        ],
        ['Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0', 'Firefox on Linux'],
        [
            'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15',
            'Safari on macOS',
        ],
        [
            'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1',
            'Safari on iOS',
        ],
        [
            'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36',
            'Chrome on Android',
        ],
    ])('names %s', (userAgent, expected) => {
        expect(describeUserAgent(userAgent)).toBe(expected);
    });

    it('says so when it cannot tell', () => {
        expect(describeUserAgent(null)).toBe('Unknown device');
        expect(describeUserAgent('curl/8.10.1')).toBe('Unknown device');
    });
});
