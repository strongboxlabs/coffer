// Tag colour palette (Tags v1). A fixed set of swatches the Tags panel
// offers; the recolor endpoint validates the #rrggbb shape (not
// membership, so the palette can evolve). A tag with no colour (null)
// renders as the theme's default gray Chip; a coloured tag renders with
// a translucent tint of its hex so the treatment reads in both light +
// dark themes without needing per-colour semantic tokens.

import type { CSSProperties } from 'react';

/** The 10-swatch palette — mid-tone hues, distinct + legible on both
 *  themes. Stored + sent lower-cased; the picker offers exactly these,
 *  plus a "no colour" (gray) option that clears back to the default. */
export const TAG_PALETTE: readonly string[] = [
    '#ef4444', // red
    '#f97316', // orange
    '#f59e0b', // amber
    '#10b981', // green
    '#14b8a6', // teal
    '#3b82f6', // blue
    '#6366f1', // indigo
    '#8b5cf6', // violet
    '#ec4899', // pink
    '#64748b', // slate
];

/** Inline style for a coloured tag chip: the hex as text over a ~13%
 *  tint (`RRGGBB` + `22` alpha) of the same hex. Returns `undefined` for
 *  a null/absent colour so the caller falls back to the default (gray)
 *  Chip variant. */
export function tagChipStyle(color: string | null | undefined): CSSProperties | undefined {
    if (!color) return undefined;
    return { backgroundColor: `${color}22`, color };
}

/** Build a lower-cased tag-name → colour map from a tag list, for the
 *  register's name-only tag chips (the resolved view carries tag names,
 *  not colours — ADR-0076 keeps the view unchanged, so colour is joined
 *  client-side from the tag list). */
export function buildTagColorMap(
    tags: ReadonlyArray<{ name: string; color: string | null }>,
): Map<string, string> {
    const map = new Map<string, string>();
    for (const t of tags) {
        if (t.color) map.set(t.name.toLowerCase(), t.color);
    }
    return map;
}
