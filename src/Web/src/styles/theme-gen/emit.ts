/**
 * Renders the palette model as the CSS that ships.
 *
 * Every block restates EVERY token. A partial block only works while the
 * attribute lives on <html>; the moment a preview nests one inside a different
 * theme, the tokens it omits are inherited from whatever it is sitting in. That
 * shipped twice — light-hc defined 39 of 43 and rendered as a light/dark hybrid
 * inside the Appearance screen.
 *
 * Selectors are compound on purpose (`[data-theme][data-accent]`), which means
 * both attributes must land on the SAME element. Splitting them across a parent
 * and a child matches nothing and silently falls back to the default palette.
 */
import { ACCENTS, build, THEMES, type AccentSlug, type ThemeSlug } from './palette';

const HEADER = `/*
 * GENERATED — do not edit. Run \`npm run themes:gen\`.
 *
 * Four themes x three colour directions. A THEME sets ground lightness and
 * contrast level; a DIRECTION is one hue that regenerates BOTH the accent
 * family and the neutral ramp (at hue+74, on slate's own lightness/chroma
 * curve). Status colours and the category palette never rotate — those carry
 * meaning, not brand.
 *
 * \`teal\` is the default: the [data-theme] blocks below are already teal, so the
 * pair blocks only cover indigo and rust.
 *
 * Values are solved in OKLCh — constant perceived chroma, so a hue rotation
 * does not change how coloured the ramp looks — and every pair the register
 * renders is audited against WCAG AA. themes.generated.test.ts fails if this
 * file drifts from the model, and refuses a combination that drops below AA.
 */
`;

function block(selector: string, scheme: 'light' | 'dark', tokens: Record<string, string>): string {
    const body = Object.keys(tokens).sort()
        .map((k) => `    --color-${k}: ${tokens[k]};`)
        .join('\n');
    return `\n${selector} {\n    color-scheme: ${scheme};\n${body}\n}\n`;
}

export function emit(): string {
    let css = HEADER;
    for (const theme of THEMES) {
        const scheme = theme.startsWith('dark') ? 'dark' : 'light';
        for (const accent of ACCENTS) {
            // `teal` is the default AND the theme blocks in index.css are the
            // hand-tuned originals, which this model reproduces to within a
            // unit per channel but not exactly. Emitting them here would
            // silently restate shipped, reviewed colours as near-misses, so it
            // does not: the generator owns the directions it introduced.
            if (accent === 'teal') continue;
            css += block(
                `[data-theme="${theme}"][data-accent="${accent}"]`,
                scheme,
                build(theme as ThemeSlug, accent as AccentSlug),
            );
        }
    }
    return css;
}
