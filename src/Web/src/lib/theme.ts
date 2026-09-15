/**
 * Theme selection — ADR-0021 Rule 4 (revised).
 *
 * Four themes: the light default, a high-contrast light, and a dark pair.
 * Each is a block of `--color-*` overrides in `index.css` keyed off
 * `data-theme` on `<html>`; Tailwind v4 emits `var(--color-x)` rather than
 * the literal, so re-theming is nothing but swapping that attribute.
 *
 * The choice is PER DEVICE, not per account: it lives in localStorage and
 * never reaches the server. Someone who wants dark on a laptop at night and
 * light on a desktop gets that for free, and there is no migration, no API
 * field, and no fetch standing between the user and first paint.
 *
 * A second, independent axis sits alongside it: the ACCENT DIRECTION. One hue
 * regenerates both the accent family and the neutral ramp, so `dark` + `rust`
 * is a different set of tokens from `dark` + `teal` — not a tint layered on
 * top. `teal` is the default and has no CSS block, because the [data-theme]
 * blocks already are teal.
 *
 * `index.html` carries a copy of the read-and-apply step as an inline script,
 * because it has to run before first paint and a module import cannot. That
 * duplication is deliberate and guarded — `theme.test.ts` fails if the two
 * drift apart.
 */

export const THEME_IDS = ['light', 'light-hc', 'dark', 'dark-hc'] as const;

export type ThemeId = (typeof THEME_IDS)[number];

export const DEFAULT_THEME: ThemeId = 'light';

/** Shared with the inline script in index.html. Changing it strands every
 *  existing user's choice, so treat it as a stored-data contract. */
export const THEME_STORAGE_KEY = 'coffer.theme';

export const THEMES: ReadonlyArray<{ id: ThemeId; label: string; hint: string }> = [
    { id: 'light', label: 'Light', hint: 'The default.' },
    {
        id: 'light-hc',
        label: 'Light high contrast',
        hint: 'Deeper ink and harder separators, for a dense register in bright light.',
    },
    { id: 'dark', label: 'Dark', hint: 'Slate ground with softer ink. Built for long sessions.' },
    {
        id: 'dark-hc',
        label: 'Dark high contrast',
        hint: 'Deeper ground, brighter ink and stronger separators.',
    },
];

/** Anything unrecognised — absent, stale, hand-edited — falls back to the
 *  default rather than leaving the app unthemed. */
export function coerceTheme(value: unknown): ThemeId {
    return THEME_IDS.includes(value as ThemeId) ? (value as ThemeId) : DEFAULT_THEME;
}

/**
 * localStorage is not always there to be read: private windows, blocked site
 * data, and some embedded webviews throw on ACCESS, not just on write. A
 * theme preference is never worth breaking a page load over, so every path
 * through here ends at a valid theme.
 */
export function readStoredTheme(): ThemeId {
    try {
        return coerceTheme(localStorage.getItem(THEME_STORAGE_KEY));
    } catch {
        return DEFAULT_THEME;
    }
}

/** Stamps the attribute the CSS keys off. Safe to call repeatedly. */
export function applyTheme(theme: ThemeId): void {
    document.documentElement.setAttribute('data-theme', theme);
}

export const ACCENT_IDS = ['teal', 'indigo', 'rust'] as const;

export type AccentId = (typeof ACCENT_IDS)[number];

export const DEFAULT_ACCENT: AccentId = 'teal';

/** Shared with the inline script in index.html, like the theme key. */
export const ACCENT_STORAGE_KEY = 'coffer.accent';

export const ACCENTS: ReadonlyArray<{ id: AccentId; label: string; hint: string }> = [
    { id: 'teal', label: 'Teal', hint: 'The default — slate ground, teal accent.' },
    { id: 'indigo', label: 'Indigo', hint: 'Cooler. A faintly violet ground under a blue accent.' },
    { id: 'rust', label: 'Rust', hint: 'Warmer. An olive-tinted ground under a burnt-orange accent.' },
];

export function coerceAccent(value: unknown): AccentId {
    return ACCENT_IDS.includes(value as AccentId) ? (value as AccentId) : DEFAULT_ACCENT;
}

export function readStoredAccent(): AccentId {
    try {
        return coerceAccent(localStorage.getItem(ACCENT_STORAGE_KEY));
    } catch {
        return DEFAULT_ACCENT;
    }
}

export function applyAccent(accent: AccentId): void {
    document.documentElement.setAttribute('data-accent', accent);
}

export function setAccent(accent: AccentId): void {
    try {
        localStorage.setItem(ACCENT_STORAGE_KEY, accent);
    } catch {
        // Blocked storage — the choice just will not survive a reload.
    }
    applyAccent(accent);
}

/** Persist + apply. Persistence is best-effort; applying is not, so the
 *  theme still changes for this session even when the write is refused. */
export function setTheme(theme: ThemeId): void {
    try {
        localStorage.setItem(THEME_STORAGE_KEY, theme);
    } catch {
        // Blocked storage — the choice just will not survive a reload.
    }
    applyTheme(theme);
}

/**
 * The third axis: DENSITY — ADR-0021 Rule 11.
 *
 * Unlike the other two this one changes no colour at all. It overrides a
 * single token, `--spacing`, which Tailwind v4 multiplies into every padding,
 * margin, gap and space utility in the app (`p-4` compiles to
 * `calc(var(--spacing) * 4)`). One variable moves ~1,700 utilities.
 *
 * It moves SPACE and nothing else. Icons, dots, swatches, control heights and
 * fixed widths are all on named `--spacing-*` tokens, which compile to a bare
 * `var(--spacing-icon-md)` and are therefore immune by construction rather
 * than by anyone remembering. That separation is the whole reason this axis
 * can exist: text does not scale here, so a control that shrank around it
 * would close on its own contents, and an icon that shrank would render soft
 * rather than small.
 */
export const DENSITY_IDS = ['compressed', 'regular', 'relaxed'] as const;

export type DensityId = (typeof DENSITY_IDS)[number];

export const DEFAULT_DENSITY: DensityId = 'regular';

/** Shared with the inline script in index.html, like the other two keys. */
export const DENSITY_STORAGE_KEY = 'coffer.density';

export const DENSITIES: ReadonlyArray<{ id: DensityId; label: string; hint: string }> = [
    {
        id: 'compressed',
        label: 'Compressed',
        hint: 'Tighter rows and padding — more of the register on screen.',
    },
    { id: 'regular', label: 'Regular', hint: 'The default.' },
    {
        id: 'relaxed',
        label: 'Relaxed',
        hint: 'Roomier padding and gaps. Controls and text are unchanged.',
    },
];

export function coerceDensity(value: unknown): DensityId {
    return DENSITY_IDS.includes(value as DensityId) ? (value as DensityId) : DEFAULT_DENSITY;
}

export function readStoredDensity(): DensityId {
    try {
        return coerceDensity(localStorage.getItem(DENSITY_STORAGE_KEY));
    } catch {
        return DEFAULT_DENSITY;
    }
}

export function applyDensity(density: DensityId): void {
    document.documentElement.setAttribute('data-density', density);
}

export function setDensity(density: DensityId): void {
    try {
        localStorage.setItem(DENSITY_STORAGE_KEY, density);
    } catch {
        // Blocked storage — the choice just will not survive a reload.
    }
    applyDensity(density);
}
