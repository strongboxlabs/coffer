import { afterEach, describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { ACCENT_STORAGE_KEY, THEME_STORAGE_KEY } from '@/lib/theme';

import { AppearancePanel } from './AppearancePanel';

// Appearance panel (ADR-0021 Rule 4, revised). Two axes — theme and colour
// direction — and the panel's job is to put valid values in localStorage and
// the matching attributes on <html>. The colours themselves are the
// stylesheet's problem; designTokens.test.ts guards those.

afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.removeAttribute('data-accent');
});

const root = () => document.documentElement;

describe('AppearancePanel', () => {
    it('offers every theme and every colour direction', () => {
        render(<AppearancePanel />);
        expect(screen.getAllByRole('radio')).toHaveLength(7);   // 4 themes + 3 colours
        for (const name of [/^light$/i, /light high contrast/i, /^dark$/i, /dark high contrast/i]) {
            expect(screen.getByRole('radio', { name })).toBeInTheDocument();
        }
        for (const name of [/^teal$/i, /^indigo$/i, /^rust$/i]) {
            expect(screen.getByRole('radio', { name })).toBeInTheDocument();
        }
    });

    it('starts on the defaults when nothing is stored', () => {
        render(<AppearancePanel />);
        expect(screen.getByRole('radio', { name: /^light$/i })).toBeChecked();
        expect(screen.getByRole('radio', { name: /^teal$/i })).toBeChecked();
    });

    it('reflects previously stored choices', () => {
        localStorage.setItem(THEME_STORAGE_KEY, 'dark-hc');
        localStorage.setItem(ACCENT_STORAGE_KEY, 'rust');
        render(<AppearancePanel />);
        expect(screen.getByRole('radio', { name: /dark high contrast/i })).toBeChecked();
        expect(screen.getByRole('radio', { name: /^rust$/i })).toBeChecked();
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

    it('applies both axes together on Save', async () => {
        const user = userEvent.setup();
        render(<AppearancePanel />);
        await user.click(screen.getByRole('radio', { name: /^dark$/i }));
        await user.click(screen.getByRole('radio', { name: /^rust$/i }));

        expect(root().getAttribute('data-theme')).not.toBe('dark');   // still staged

        await user.click(screen.getByRole('button', { name: /^save$/i }));

        expect(root().getAttribute('data-theme')).toBe('dark');
        expect(root().getAttribute('data-accent')).toBe('rust');
        expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
        expect(localStorage.getItem(ACCENT_STORAGE_KEY)).toBe('rust');
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

    it('carries both attributes on ONE preview element', () => {
        // The stylesheet's selectors are compound —
        // [data-theme="dark"][data-accent="rust"] — so splitting the two across
        // a parent and a child matches nothing and every preview silently
        // renders the default palette. That shipped, and made all three colour
        // previews look identical.
        const { container } = render(<AppearancePanel />);
        const previews = container.querySelectorAll('span[data-theme][data-accent]');
        expect(previews).toHaveLength(7);
        for (const el of previews) {
            expect(el.getAttribute('data-theme')).toBeTruthy();
            expect(el.getAttribute('data-accent')).toBeTruthy();
        }
    });

    it('previews each colour in the staged theme, not a fixed one', async () => {
        const user = userEvent.setup();
        const { container } = render(<AppearancePanel />);
        await user.click(screen.getByRole('radio', { name: /dark high contrast/i }));

        // The three colour previews follow the staged theme; a fixed preview
        // would misrepresent them, since the same hue is a different colour on
        // a light ground than a dark one.
        const accents = [...container.querySelectorAll('span[data-theme][data-accent]')]
            .filter((el) => el.getAttribute('data-theme') === 'dark-hc');
        expect(accents.map((el) => el.getAttribute('data-accent')))
            .toEqual(expect.arrayContaining(['teal', 'indigo', 'rust']));
    });
});
