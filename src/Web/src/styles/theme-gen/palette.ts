/**
 * The palette model: 4 themes x 3 colour directions, solved and audited.
 *
 * THE MEASUREMENT THIS RESTS ON. All four themes draw from ONE slate scale, at
 * different positions. Chroma is a function of lightness along it — ~0.040
 * through the middle, tapering to 0.000 at white. That taper is what keeps a
 * tinted near-white from going cream, and a direction inherits it for free by
 * rotating the same curve.
 *
 * So a THEME is an assignment of tokens to positions on the scale, and a
 * DIRECTION is the hue that scale sits at. Neither invents values.
 *
 * WHAT A DIRECTION DOES NOT TOUCH: status colours and the category palette.
 * Those carry meaning, not brand — see semantic.ts.
 */
import { maxChroma, oklchToHex } from './oklch';
import { SEMANTIC } from './semantic';

export const THEMES = ['light', 'light-hc', 'dark', 'dark-hc'] as const;
export const ACCENTS = ['teal', 'indigo', 'rust'] as const;

export type ThemeSlug = (typeof THEMES)[number];
export type AccentSlug = (typeof ACCENTS)[number];

/** token -> [lightness, chroma], measured off each shipping theme. */
const RAMPS: Record<ThemeSlug, ReadonlyArray<readonly [string, number, number]>> = {
    'light': [['surface', 1.000, 0.0000], ['surface-muted', 0.984, 0.0034],
        ['surface-hover', 0.929, 0.0126], ['surface-header', 0.968, 0.0069],
        ['surface-sidebar', 1.000, 0.0000], ['border', 0.869, 0.0198],
        ['border-strong', 0.711, 0.0351], ['text', 0.208, 0.0398],
        ['text-muted', 0.446, 0.0374], ['text-subtle', 0.554, 0.0407]],
    'light-hc': [['surface', 1.000, 0.0000], ['surface-muted', 0.984, 0.0034],
        ['surface-hover', 0.869, 0.0198], ['surface-header', 0.929, 0.0126],
        ['surface-sidebar', 1.000, 0.0000], ['border', 0.711, 0.0351],
        ['border-strong', 0.554, 0.0407], ['text', 0.129, 0.0406],
        ['text-muted', 0.372, 0.0392], ['text-subtle', 0.446, 0.0374]],
    'dark': [['surface', 0.279, 0.0368], ['surface-muted', 0.208, 0.0398],
        ['surface-hover', 0.372, 0.0392], ['surface-header', 0.208, 0.0398],
        ['surface-sidebar', 0.279, 0.0368], ['border', 0.446, 0.0374],
        ['border-strong', 0.554, 0.0407], ['text', 0.929, 0.0126],
        ['text-muted', 0.711, 0.0351], ['text-subtle', 0.664, 0.0350]],
    'dark-hc': [['surface', 0.208, 0.0398], ['surface-muted', 0.129, 0.0406],
        ['surface-hover', 0.279, 0.0368], ['surface-header', 0.279, 0.0368],
        ['surface-sidebar', 0.208, 0.0398], ['border', 0.446, 0.0374],
        ['border-strong', 0.711, 0.0351], ['text', 0.968, 0.0069],
        ['text-muted', 0.869, 0.0198], ['text-subtle', 0.711, 0.0351]],
};

/** accent-as-text contrast on that theme's surface, measured off shipping teal. */
const THEME_ACCENT: Record<ThemeSlug, number> = {
    'light': 5.47, 'light-hc': 6.90, 'dark': 7.86, 'dark-hc': 9.59,
};

/**
 * A direction is a hue. The second number scales the theme's accent target on
 * DARK grounds only: blue carries ~7% of luminance against green's ~72%, so
 * indigo held to teal's ratio turns pale lavender. On light the asymmetry runs
 * the other way — scaling down there just makes the accent too faint, and it
 * put indigo at 4.01:1 and rust at 4.48:1 on white, both under AA.
 */
const DIRECTIONS: Record<AccentSlug, readonly [number, number]> = {
    teal: [184.0, 1.00], indigo: [264.0, 0.72], rust: [45.0, 0.82],
};

/** Neutral hue sits this far from the accent — the teal/slate relationship the
 *  shipping design already encodes, generalised. */
const NEUTRAL_OFFSET = 74.0;

/** [fill L, fill C, text L, text C] for the neutral chip, per theme. */
const CHIP: Record<ThemeSlug, readonly [number, number, number, number]> = {
    'light': [0.929, 0.0126, 0.446, 0.0374],
    'light-hc': [0.869, 0.0198, 0.372, 0.0392],
    'dark': [0.208, 0.0398, 0.860, 0.0350],
    'dark-hc': [0.129, 0.0406, 0.860, 0.0350],
};

// --- contrast ---------------------------------------------------------------

function channel(c: number): number {
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

export function luminance(hex: string): number {
    const h = hex.replace('#', '');
    const [r, g, b] = [0, 2, 4].map((i) => channel(parseInt(h.slice(i, i + 2), 16) / 255));
    return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

export function ratio(fg: string, bg: string): number {
    const [a, b] = [luminance(fg), luminance(bg)];
    return (Math.max(a!, b!) + 0.05) / (Math.min(a!, b!) + 0.05);
}

/** Composite `fg` at `alpha` over opaque `bg`, as the browser does for
 *  `color-mix(..., transparent)` and `rgb(r g b / a)`. */
export function over(fg: string, bg: string, alpha: number): string {
    const [f, b] = [fg, bg].map((c) =>
        [0, 2, 4].map((i) => parseInt(c.replace('#', '').slice(i, i + 2), 16)));
    return `#${[0, 1, 2]
        .map((i) => Math.round(f![i]! * alpha + b![i]! * (1 - alpha))
            .toString(16).padStart(2, '0'))
        .join('')}`;
}

// --- solving ----------------------------------------------------------------

/**
 * Most-saturated colour at `hue` whose contrast against `against` is closest to
 * `target`. Chroma comes from what the hue can actually HOLD at each lightness
 * rather than a fixed value — see the note on `inGamut`.
 */
function solve(
    hue: number, chromaCap: number, target: number, against: string, lighter: boolean,
): string {
    let best: { hex: string; d: number } | null = null;
    for (let i = 4; i < 99; i += 1) {
        const L = i / 100;
        const C = Math.min(chromaCap, maxChroma(L, hue) * 0.92);
        if (C <= 0.001) continue;
        const hex = oklchToHex(L, C, hue);
        if (lighter !== luminance(hex) > luminance(against)) continue;
        const d = Math.abs(ratio(hex, against) - target);
        if (best === null || d < best.d) best = { hex, d };
    }
    if (best === null) throw new Error(`no solution at hue ${hue} target ${target}`);
    return best.hex;
}

export type Tokens = Record<string, string>;

export function build(theme: ThemeSlug, accent: AccentSlug): Tokens {
    const [hue, scale] = DIRECTIONS[accent];
    const nh = (hue + NEUTRAL_OFFSET) % 360;
    const t: Tokens = { ...SEMANTIC[theme]! };

    for (const [key, L, C] of RAMPS[theme]) t[key] = oklchToHex(L, C, nh);

    const darkbg = luminance(t['surface']!) < 0.2;
    t['text-inverse'] = darkbg ? '#020617' : '#f8fafc';
    const target = THEME_ACCENT[theme] * (darkbg ? scale : 1);
    t['accent'] = solve(hue, 0.13, target, t['surface']!, darkbg);
    t['accent-hover'] = solve(hue, 0.13, target * (darkbg ? 1.25 : 0.78), t['surface']!, darkbg);
    t['accent-soft'] = solve(hue, 0.06, 1.35, t['surface']!, darkbg);
    t['accent-soft-text'] = solve(hue, 0.12, 5.5, t['accent-soft']!, darkbg);
    const [bl, bc, tl, tc] = CHIP[theme];
    t['chip-neutral'] = oklchToHex(bl, bc, nh);
    t['chip-neutral-text'] = oklchToHex(tl, tc, nh);
    return t;
}

// --- audit ------------------------------------------------------------------

const CATS = ['groc', 'din', 'house', 'util', 'sub', 'tran', 'sal', 'xfer', 'phone', 'rec'];

/** WCAG AA. 10px and 11px labels are still "normal text" — the 3:1 large-text
 *  allowance starts at 18.66px bold / 24px regular. */
export const AA = 4.5;

/**
 * Every text/background pair the register actually renders, INCLUDING the
 * composited row-state fills — the tinted background is what the text sits on,
 * and it is where the first cut of the dark themes failed.
 */
export function audit(t: Tokens): Array<{ pair: string; value: number }> {
    const s = t['surface']!;
    const nest = over(t['surface-muted']!, s, 0.40);
    const sel = over(t['accent-soft']!, s, 0.40);
    const nr = over(t['state-warning-soft']!, s, 0.70);
    const pairs: Array<[string, string, string]> = [
        ['body text', t['text']!, s],
        ['muted text', t['text-muted']!, s],
        ['subtle text', t['text-subtle']!, s],
        ['footer on canvas', t['text-subtle']!, t['surface-muted']!],
        ['column header strip', t['text-muted']!, t['surface-header']!],
        ['memo on needs-review row', t['text-muted']!, nr],
        ['memo on selected row', t['text-muted']!, sel],
        ['amount on surface', t['state-danger']!, s],
        ['amount on needs-review row', t['state-danger']!, nr],
        ['amount on selected row', t['state-danger']!, sel],
        ['amount on nested row', t['state-danger']!, nest],
        ['cleared badge', t['accent-soft-text']!, t['accent-soft']!],
        ['pending badge', t['state-warning']!, t['state-warning-soft']!],
        ['tag chip', t['chip-neutral-text']!, t['chip-neutral']!],
        ['row hover', t['text']!, t['surface-hover']!],
        ['accent as text', t['accent']!, s],
        ['label on accent fill', t['text-inverse']!, t['accent']!],
        ['label on danger fill', t['text-inverse']!, t['state-danger']!],
        ['accent link on sidebar', t['accent']!, t['surface-sidebar']!],
        ['sidebar text', t['text']!, t['surface-sidebar']!],
        ['sidebar muted text', t['text-muted']!, t['surface-sidebar']!],
    ];
    for (const c of CATS) pairs.push([`${c} chip`, t[`cat-${c}-text`]!, t[`cat-${c}-soft`]!]);

    return pairs
        .map(([pair, fg, bg]) => ({ pair, value: ratio(fg, bg) }))
        .filter(({ value }) => value < AA);
}
