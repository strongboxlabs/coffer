import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

// The API sends a Content-Security-Policy with style-src 'self' and script-src 'self'
// (FU-091). Anything that puts a <style> element, inline script or eval into the page is
// refused by the browser — silently, as far as the user is concerned: RecoveryCodes'
// print stylesheet was one, and printing the codes would have printed the whole page.

const SRC = join(import.meta.dirname, '..');

function sources(dir: string): string[] {
    return readdirSync(dir).flatMap((name) => {
        const path = join(dir, name);
        if (statSync(path).isDirectory()) return sources(path);
        return /\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name) ? [path] : [];
    });
}

describe('the SPA under the Content-Security-Policy', () => {
    const files = sources(SRC);

    it('scans the app, not an empty directory', () => {
        expect(files.length).toBeGreaterThan(100);
    });

    it('renders no <style> element', () => {
        const offenders = files.filter((f) => /<style[\s>]/.test(readFileSync(f, 'utf8')));
        expect(offenders.map((f) => relative(SRC, f))).toEqual([]);
    });

    it('builds no code from strings', () => {
        const offenders = files.filter((f) => /\beval\(|\bnew Function\(/.test(readFileSync(f, 'utf8')));
        expect(offenders.map((f) => relative(SRC, f))).toEqual([]);
    });
});
