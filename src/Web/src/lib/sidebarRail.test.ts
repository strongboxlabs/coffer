import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import {
    clampWidth,
    DEFAULT_WIDTH,
    MAX_VIEWPORT_SHARE,
    MAX_WIDTH,
    maxWidthFor,
    MIN_WIDTH,
    readClosedFolders,
    readStoredWidth,
    saveClosedFolders,
    saveWidth,
    WIDTH_STORAGE_KEY,
    WIDTH_VAR,
} from './sidebarRail';

const THEME_INIT = join(import.meta.dirname, '..', '..', 'public', 'theme-init.js');

afterEach(() => {
    localStorage.clear();
    document.documentElement.style.removeProperty(WIDTH_VAR);
});

describe('the rail width', () => {
    it('holds between 200px and the smaller of 480px and 40% of the window', () => {
        expect(maxWidthFor(2000)).toBe(MAX_WIDTH);
        expect(maxWidthFor(800)).toBe(320);
        // A window too narrow for even the minimum still gets the minimum.
        expect(maxWidthFor(300)).toBe(MIN_WIDTH);
        expect(clampWidth(100, 2000)).toBe(MIN_WIDTH);
        expect(clampWidth(900, 2000)).toBe(MAX_WIDTH);
        expect(clampWidth(260.6, 2000)).toBe(261);
        expect(clampWidth(Number.NaN, 2000)).toBe(DEFAULT_WIDTH);
    });

    it('defaults to the rail it always had, and stores the default as nothing', () => {
        expect(DEFAULT_WIDTH).toBe(224);
        expect(readStoredWidth()).toBeNull();
        saveWidth(300);
        expect(readStoredWidth()).toBe(300);
        saveWidth(DEFAULT_WIDTH);
        expect(localStorage.getItem(WIDTH_STORAGE_KEY)).toBeNull();
    });

    it('clamps a stored width to this window on read, and ignores junk', () => {
        localStorage.setItem(WIDTH_STORAGE_KEY, '5000');
        expect(readStoredWidth()).toBe(maxWidthFor(window.innerWidth));
        localStorage.setItem(WIDTH_STORAGE_KEY, 'wide');
        expect(readStoredWidth()).toBeNull();
    });
});

// The collapsed flag is usePersistedDisclosure's; AuthedSidebar.test.tsx holds
// the rail to it (collapse, reload, expand).
describe('folder state', () => {
    it('round-trips, per ledger', () => {
        saveClosedFolders('l1', new Set(['f1', 'f2']));
        expect([...readClosedFolders('l1')].sort()).toEqual(['f1', 'f2']);
        expect(readClosedFolders('l2').size).toBe(0);
    });

    it('reads a corrupted folder list as nothing closed', () => {
        localStorage.setItem('coffer.sidebar.closedFolders.l1', '{not json');
        expect(readClosedFolders('l1').size).toBe(0);
    });
});

describe('the pre-paint copy in public/theme-init.js', () => {
    // A hand-written copy of readStoredWidth + clampWidth, because it runs before
    // any module can. These hold the copy to the original, the way theme.test.ts
    // holds the theme half of the same file.
    const script = readFileSync(THEME_INIT, 'utf8');
    const run = () => new Function(script)();

    it('uses the same key, variable and limits', () => {
        expect(script).toContain(`'${WIDTH_STORAGE_KEY}'`);
        expect(script).toContain(`'${WIDTH_VAR}'`);
        expect(script).toContain(`Math.max(${MIN_WIDTH}, Math.min(${MAX_WIDTH}, Math.floor(window.innerWidth * ${MAX_VIEWPORT_SHARE})))`);
    });

    it('applies a stored width, clamped exactly as the module clamps it', () => {
        localStorage.setItem(WIDTH_STORAGE_KEY, '5000');
        run();
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR))
            .toBe(`${readStoredWidth()}px`);

        localStorage.setItem(WIDTH_STORAGE_KEY, '250');
        run();
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe('250px');
    });

    it('sets nothing when nothing usable is stored, so the token stays in charge', () => {
        run();
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe('');
        localStorage.setItem(WIDTH_STORAGE_KEY, 'wide');
        run();
        expect(document.documentElement.style.getPropertyValue(WIDTH_VAR)).toBe('');
    });
});
