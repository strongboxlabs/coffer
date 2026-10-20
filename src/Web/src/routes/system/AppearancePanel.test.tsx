import { afterEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { ACCENT_STORAGE_KEY, DENSITY_STORAGE_KEY, THEME_STORAGE_KEY } from '@/lib/theme';

import { AppearancePanel } from './AppearancePanel';

// Appearance panel (ADR-0021 Rule 4 revised, and Rule 11a). Three axes —
// theme, colour direction and density — and the panel's job is to put valid
// values in localStorage and the matching attributes on <html>. The colours
// themselves are the stylesheet's problem (designTokens.test.ts); which
// lengths density may move is pinnedLengths.test.ts's.

afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.removeAttribute('data-accent');
    document.documentElement.removeAttribute('data-density');
});

const root = () => document.documentElement;

describe('AppearancePanel', () => {
    it('offers every theme, every colour direction and every density', () => {
        render(<AppearancePanel />);
        // 4 themes + 3 colours + 3 densities
        expect(screen.getAllByRole('radio')).toHaveLength(10);
        for (const name of [/^light$/i, /light high contrast/i, /^dark$/i, /dark high contrast/i]) {
            expect(screen.getByRole('radio', { name })).toBeInTheDocument();
        }
        for (const name of [/^teal$/i, /^indigo$/i, /^rust$/i]) {
            expect(screen.getByRole('radio', { name })).toBeInTheDocument();
        }
        for (const name of [/^compressed$/i, /^regular$/i, /^relaxed$/i]) {
            expect(screen.getByRole('radio', { name })).toBeInTheDocument();
        }
    });

    it('starts on the defaults when nothing is stored', () => {
        render(<AppearancePanel />);
        expect(screen.getByRole('radio', { name: /^light$/i })).toBeChecked();
        expect(screen.getByRole('radio', { name: /^teal$/i })).toBeChecked();
        expect(screen.getByRole('radio', { name: /^regular$/i })).toBeChecked();
    });

    it('reflects previously stored choices', () => {
        localStorage.setItem(THEME_STORAGE_KEY, 'dark-hc');
        localStorage.setItem(ACCENT_STORAGE_KEY, 'rust');
        localStorage.setItem(DENSITY_STORAGE_KEY, 'compressed');
        render(<AppearancePanel />);
        expect(screen.getByRole('radio', { name: /dark high contrast/i })).toBeChecked();
        expect(screen.getByRole('radio', { name: /^rust$/i })).toBeChecked();
        expect(screen.getByRole('radio', { name: /^compressed$/i })).toBeChecked();
    });

    it('does NOT apply on selection', async () => {
        // The behaviour this panel exists to avoid: selecting used to repaint
        // the whole app, so clicking down a list of four themes strobed it.
        const user = userEvent.setup();
        localStorage.setItem(THEME_STORAGE_KEY, 'light');
        render(<AppearancePanel />);

        // Anchor on the applied state BEFORE the click, so the assertion after
        // it cannot pass just because nothing was ever set.
        expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');

        await user.click(screen.getByRole('radio', { name: /^dark$/i }));

        expect(screen.getByRole('radio', { name: /^dark$/i })).toBeChecked();
        expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
        expect(root().getAttribute('data-theme')).not.toBe('dark');
    });

    it('applies all three axes together on Save', async () => {
        const user = userEvent.setup();
        render(<AppearancePanel />);
        await user.click(screen.getByRole('radio', { name: /^dark$/i }));
        await user.click(screen.getByRole('radio', { name: /^rust$/i }));
        await user.click(screen.getByRole('radio', { name: /^compressed$/i }));

        expect(root().getAttribute('data-theme')).not.toBe('dark');   // still staged
        expect(root().getAttribute('data-density')).not.toBe('compressed');

        await user.click(screen.getByRole('button', { name: /^save$/i }));

        expect(root().getAttribute('data-theme')).toBe('dark');
        expect(root().getAttribute('data-accent')).toBe('rust');
        expect(root().getAttribute('data-density')).toBe('compressed');
        expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
        expect(localStorage.getItem(ACCENT_STORAGE_KEY)).toBe('rust');
        expect(localStorage.getItem(DENSITY_STORAGE_KEY)).toBe('compressed');
    });

    it('Cancel drops the draft and leaves the applied choice alone', async () => {
        const user = userEvent.setup();
        localStorage.setItem(THEME_STORAGE_KEY, 'light-hc');
        render(<AppearancePanel />);

        await user.click(screen.getByRole('radio', { name: /^dark$/i }));
        expect(screen.getByRole('radio', { name: /^dark$/i })).toBeChecked();

        await user.click(screen.getByRole('button', { name: /cancel/i }));

        expect(screen.getByRole('radio', { name: /light high contrast/i })).toBeChecked();
        expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light-hc');
    });

    it('disables Save until something changes', async () => {
        const user = userEvent.setup();
        render(<AppearancePanel />);
        expect(screen.getByRole('button', { name: /^save$/i })).toBeDisabled();

        await user.click(screen.getByRole('radio', { name: /^indigo$/i }));
        expect(screen.getByRole('button', { name: /^save$/i })).toBeEnabled();
    });

    it('carries all three attributes on ONE preview element', () => {
        // The stylesheet's colour selectors are compound —
        // [data-theme="dark"][data-accent="rust"] — so splitting the two across
        // a parent and a child matches nothing and every preview silently
        // renders the default palette. That shipped, and made all three colour
        // previews look identical. Density rides the same element: it needs no
        // pairing, but a preview that inherited the page's spacing instead of
        // showing its own would be the same class of lie.
        const { container } = render(<AppearancePanel />);
        const previews = container.querySelectorAll('span[data-theme][data-accent][data-density]');
        expect(previews).toHaveLength(10);
        for (const el of previews) {
            expect(el.getAttribute('data-theme')).toBeTruthy();
            expect(el.getAttribute('data-accent')).toBeTruthy();
            expect(el.getAttribute('data-density')).toBeTruthy();
        }
    });

    it('moves NOTHING on the page or in any other preview when you select', async () => {
        // The reported bug, and the reason it is worth a test rather than a
        // comment. Nothing was ever SAVED on selection — the root attributes
        // were always correct. What made it read as "applied on the fly" was
        // that every preview followed every draft, so one density click
        // repainted 7 of the 10 cards and one theme click darkened 6 of them.
        // Staged means staged: a click moves the radio and nothing else.
        const user = userEvent.setup();
        const { container } = render(<AppearancePanel />);

        const snapshot = () =>
            [...container.querySelectorAll('span[data-theme][data-accent][data-density]')]
                .map((el) => [
                    el.getAttribute('data-theme'),
                    el.getAttribute('data-accent'),
                    el.getAttribute('data-density'),
                ].join('/'));

        // Anchor on a populated snapshot, so this cannot pass on an empty list.
        const before = snapshot();
        expect(before).toHaveLength(10);

        for (const name of [/^dark$/i, /^rust$/i, /^compressed$/i]) {
            await user.click(screen.getByRole('radio', { name }));
            expect(snapshot()).toEqual(before);
        }

        // ...and the page itself is untouched until Save.
        expect(root().getAttribute('data-theme')).not.toBe('dark');
        expect(root().getAttribute('data-accent')).not.toBe('rust');
        expect(root().getAttribute('data-density')).not.toBe('compressed');
    });

    it('gives each axis a preview that varies only that axis', () => {
        const { container } = render(<AppearancePanel />);
        const cardFor = (name: RegExp) =>
            screen.getByRole('radio', { name }).closest('label')!
                .querySelector('span[data-density]')!;

        // Each row differs from its neighbours in ONE attribute.
        expect(cardFor(/^dark$/i).getAttribute('data-theme')).toBe('dark');
        expect(cardFor(/^dark$/i).getAttribute('data-accent')).toBe('teal');
        expect(cardFor(/^rust$/i).getAttribute('data-accent')).toBe('rust');
        expect(cardFor(/^rust$/i).getAttribute('data-theme')).toBe('light');
        expect(cardFor(/^compressed$/i).getAttribute('data-density')).toBe('compressed');
        expect(cardFor(/^compressed$/i).getAttribute('data-theme')).toBe('light');
        void container;
    });

    it('gives the density previews enough rows to show density', () => {
        // At one row the three densities were 16 / 20 / 24px tall — a 4px
        // spread beside a two-line label, which is why they read as identical.
        // Density is cumulative; the sample has to be tall enough to accumulate.
        const { container } = render(<AppearancePanel />);
        const rowsIn = (name: RegExp) =>
            screen.getByRole('radio', { name }).closest('label')!
                .querySelectorAll('span.font-mono').length;

        for (const name of [/^compressed$/i, /^regular$/i, /^relaxed$/i]) {
            expect(rowsIn(name)).toBeGreaterThanOrEqual(6);
        }
        // Colour and theme rows stay compact — they read fine from one row.
        expect(rowsIn(/^dark$/i)).toBe(1);
        expect(rowsIn(/^rust$/i)).toBe(1);
        void container;
    });
});
