import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
    ACCENT_IDS,
    ACCENT_STORAGE_KEY,
    applyTheme,
    coerceAccent,
    coerceTheme,
    DEFAULT_ACCENT,
    DEFAULT_THEME,
    readStoredAccent,
    readStoredTheme,
    setAccent,
    setTheme,
    THEME_IDS,
    THEME_STORAGE_KEY,
    THEMES,
} from './theme';

const INDEX_HTML = join(import.meta.dirname, '..', '..', 'index.html');

afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    vi.restoreAllMocks();
});

describe('coerceTheme', () => {
    it('accepts every shipped theme id', () => {
        for (const id of THEME_IDS) expect(coerceTheme(id)).toBe(id);
    });

    it('falls back for absent, stale or hand-edited values', () => {
        for (const bad of [null, undefined, '', 'solarized', 'DARK', 42, {}]) {
            expect(coerceTheme(bad)).toBe(DEFAULT_THEME);
        }
    });
});

describe('readStoredTheme', () => {
    it('reads a stored choice', () => {
        localStorage.setItem(THEME_STORAGE_KEY, 'dark-hc');
        expect(readStoredTheme()).toBe('dark-hc');
    });

    it('returns the default when nothing is stored', () => {
        expect(readStoredTheme()).toBe(DEFAULT_THEME);
    });

    it('survives storage that throws on read', () => {
        // Private windows and some embedded webviews throw on ACCESS, not just
        // on write. A theme preference must never take the page down with it.
        vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
            throw new DOMException('denied', 'SecurityError');
        });
        expect(readStoredTheme()).toBe(DEFAULT_THEME);
    });
});

describe('setTheme', () => {
    it('persists the choice and stamps the attribute the CSS keys off', () => {
        setTheme('dark');
        expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
        expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    });

    it('still applies when the write is refused', () => {
        vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
            throw new DOMException('quota', 'QuotaExceededError');
        });
        setTheme('light-hc');
        expect(document.documentElement.getAttribute('data-theme')).toBe('light-hc');
    });
});

describe('applyTheme', () => {
    it('is idempotent', () => {
        applyTheme('dark');
        applyTheme('dark');
        expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    });
});

describe('the pre-paint script in index.html', () => {
    // index.html carries a hand-written copy of read-and-apply, because it has
    // to run before the first paint and a module import cannot. Nothing in the
    // build ties the two together, so a theme added here would keep flashing
    // the light theme on load with no error anywhere. These assertions are the
    // only thing holding the copy to the original.
    const html = readFileSync(INDEX_HTML, 'utf8');

    it('uses the same storage key as the module', () => {
        expect(html).toContain(`'${THEME_STORAGE_KEY}'`);
    });

    it('knows every theme the module ships', () => {
        for (const id of THEME_IDS) expect(html).toContain(`'${id}'`);
    });

    it('falls back to the same default', () => {
        expect(html).toContain(`var fallback = '${DEFAULT_THEME}'`);
    });

    it('uses the same accent storage key', () => {
        expect(html).toContain(`'${ACCENT_STORAGE_KEY}'`);
    });

    it('knows every accent direction the module ships', () => {
        for (const id of ACCENT_IDS) expect(html).toContain(`'${id}'`);
    });

    it('falls back to the same accent default', () => {
        expect(html).toContain(`var accentFallback = '${DEFAULT_ACCENT}'`);
    });

    it('stamps BOTH attributes before paint', () => {
        // The stylesheet's pair selectors are compound, so a load that set only
        // data-theme would render the default palette until React mounted —
        // a flash of the wrong colour, not just the wrong theme.
        expect(html).toContain("setAttribute('data-theme'");
        expect(html).toContain("setAttribute('data-accent'");
    });

    it('runs before the stylesheet-dependent app script', () => {
        // Ordering is the whole point: after /src/main.tsx it would still work,
        // but only after a visible flash of the wrong theme.
        expect(html.indexOf('data-theme')).toBeLessThan(html.indexOf('/src/main.tsx'));
    });
});

describe('the accent axis', () => {
    it('accepts every shipped direction and rejects the rest', () => {
        for (const id of ACCENT_IDS) expect(coerceAccent(id)).toBe(id);
        for (const bad of [null, undefined, '', 'violet', 'TEAL', 7, {}]) {
            expect(coerceAccent(bad)).toBe(DEFAULT_ACCENT);
        }
    });

    it('reads, persists and applies independently of the theme', () => {
        setTheme('dark');
        setAccent('rust');
        expect(localStorage.getItem(ACCENT_STORAGE_KEY)).toBe('rust');
        expect(document.documentElement.getAttribute('data-accent')).toBe('rust');
        expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
        expect(readStoredAccent()).toBe('rust');
    });

    it('survives storage that throws on read', () => {
        vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
            throw new DOMException('denied', 'SecurityError');
        });
        expect(readStoredAccent()).toBe(DEFAULT_ACCENT);
    });
});

describe('THEMES', () => {
    it('lists every id exactly once, in id order', () => {
        expect(THEMES.map((t) => t.id)).toEqual([...THEME_IDS]);
    });

    it('gives each theme a label and a hint', () => {
        for (const t of THEMES) {
            expect(t.label.length).toBeGreaterThan(0);
            expect(t.hint.length).toBeGreaterThan(0);
        }
    });
});
