// Tag colour palette (Tags v1). A fixed set of swatches the Tags panel
// offers; the recolor endpoint validates the #rrggbb shape (not
// membership, so the palette can evolve). A tag with no colour (null)
// renders as the theme's default gray Chip.
//
// A COLOURED tag renders as a SOLID fill of its hex with a black or white
// label, whichever contrasts more.
//
// It used to be the hex as text over a ~13% tint of the same hex. That reads
// nicely and is unfixable: both sides of the pair move together, so there is
// no contrast term in it. Measured against the shipping themes, 33 of 40
// swatch/theme combinations were below WCAG AA — on light, ALL TEN failed, with
// amber at 1.94:1. It went unnoticed because tag colours are user DATA, so they
// sit outside the token audit by construction.
//
// Solid fill fixes it by construction rather than by tuning. Contrast then
// depends only on the hex, not on the surface, so it is immune to the theme and
// colour-direction matrix — and optimal pure black/white bottoms out at ~4.58:1
// at the luminance crossover, so EVERY colour a user can pick clears AA. The
// cost is that a coloured tag is louder than a tinted one; that is the right
// trade for a label whose entire job is to be picked out at a glance.

import type { CSSProperties } from 'react';

import { readableOn } from './contrast';

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

/** Inline style for a coloured tag chip: a solid fill of the hex with the
 *  more legible of black/white as the label. Returns `undefined` for a
 *  null/absent colour so the caller falls back to the default (gray) Chip
 *  variant. */
export function tagChipStyle(color: string | null | undefined): CSSProperties | undefined {
    if (!color) return undefined;
    return { backgroundColor: color, color: readableOn(color) };
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
