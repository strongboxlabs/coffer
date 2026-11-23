import { categoryChipVariant } from '@/lib/categoryChip';
import type { ReportGroupBy, ReportRest, ReportSpec } from '@/types/report';

import { groupNameParts, type GroupNameParts } from './groupLabel';

/**
 * Which colour each series of a report chart takes. Nine families, then deeper
 * shades of them, then repeats: the colour count never limits how many series a
 * chart draws.
 *
 * THE HUES ARE THE CATEGORY FAMILIES. Nine of the ten: slate is not a series
 * colour, because it is Other's, and a group drawn in slate would read as part
 * of the fold. Each family has a chart shade per theme (`--color-cat-*-chart`),
 * distinct from the chip's text shade, which is tuned for words and too close
 * to its neighbours to tell apart as a fill.
 *
 * UNIQUE WITHIN A CHART. A category keeps the colour the rest of the app gives
 * it (its chip and its dot), so the same category is the same family on the
 * register and on the chart. When two categories share a colour, the larger
 * keeps it and the smaller borrows one no group in the chart holds. Payees,
 * accounts, tags and kinds have no colour of their own, so they take hues by
 * rank. A tag's user colour is user data outside the token set, and is not
 * used.
 */

export const CHART_HUES = ['groc', 'din', 'house', 'util', 'sub', 'tran', 'sal', 'phone', 'rec'] as const;
export type ChartHue = (typeof CHART_HUES)[number];

/**
 * The `topN` that cuts nothing: the largest the server's whole number takes.
 *
 * A chart over "All" still sends a `topN`, because the server marks which rows
 * are drawn only when it is asked to cut, and a rolled-up parent's own postings
 * are found by that mark. No cut means no Other.
 */
export const ALL_GROUPS = 2_147_483_647;

/**
 * The `topN` a chart asks for: the report's own cap, or every group. The colour
 * count never decides it. A series past the nine hues takes a deeper shade of
 * one (`SHADES`), and past those the colours repeat, so "All" is all.
 */
export function chartTopN(spec: Pick<ReportSpec, 'topN'>): number {
    return spec.topN ?? ALL_GROUPS;
}

/**
 * How many times a hue is drawn before it repeats: the family's own shade, then
 * two deeper ones mixed toward the theme's text colour. Toward the text, so a
 * shade only gains contrast on the card in either theme.
 */
export const SHADES = 3;

/**
 * Fill classes, written out: Tailwind finds classes by scanning source text,
 * so a class name assembled from the hue at run time would never be generated.
 * Index 0 is the family's own token; 1 and 2 mix it 65% and 35% with the text.
 */
const FILL: Readonly<Record<ChartHue, readonly [string, string, string]>> = {
    groc: ['fill-cat-groc-chart', 'fill-[color-mix(in_srgb,var(--color-cat-groc-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-groc-chart)_35%,var(--color-text))]'],
    din: ['fill-cat-din-chart', 'fill-[color-mix(in_srgb,var(--color-cat-din-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-din-chart)_35%,var(--color-text))]'],
    house: ['fill-cat-house-chart', 'fill-[color-mix(in_srgb,var(--color-cat-house-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-house-chart)_35%,var(--color-text))]'],
    util: ['fill-cat-util-chart', 'fill-[color-mix(in_srgb,var(--color-cat-util-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-util-chart)_35%,var(--color-text))]'],
    sub: ['fill-cat-sub-chart', 'fill-[color-mix(in_srgb,var(--color-cat-sub-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-sub-chart)_35%,var(--color-text))]'],
    tran: ['fill-cat-tran-chart', 'fill-[color-mix(in_srgb,var(--color-cat-tran-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-tran-chart)_35%,var(--color-text))]'],
    sal: ['fill-cat-sal-chart', 'fill-[color-mix(in_srgb,var(--color-cat-sal-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-sal-chart)_35%,var(--color-text))]'],
    phone: ['fill-cat-phone-chart', 'fill-[color-mix(in_srgb,var(--color-cat-phone-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-phone-chart)_35%,var(--color-text))]'],
    rec: ['fill-cat-rec-chart', 'fill-[color-mix(in_srgb,var(--color-cat-rec-chart)_65%,var(--color-text))]', 'fill-[color-mix(in_srgb,var(--color-cat-rec-chart)_35%,var(--color-text))]'],
};

/** A hue's fill at a shade; a shade past the last wraps to the first. */
export const hueFill = (hue: ChartHue, shade = 0): string => FILL[hue][shade % SHADES]!;

/** Other's fill: the slate no series can take. */
export const OTHER_FILL = 'fill-cat-xfer-text';

/**
 * The ranked view's one fill. Each bar has its name beside it, so a colour
 * would carry nothing; it is the neutral the home charts draw in.
 */
export const RANKED_BG = 'bg-chip-neutral-text/60';

const isHue = (v: string): v is ChartHue => (CHART_HUES as readonly string[]).includes(v);

export interface SeriesIdentity {
    key: string;
    /** A category's account id; null where the dimension has none. */
    groupId: string | null;
    /** The group's own name, never a display label built from it. */
    name: string;
    /** The ledger's system Uncategorized, whose own colour is the neutral one. */
    isSystemCategory: boolean;
}

/**
 * One hue per series, in the order given, which is largest first.
 *
 * Category charts take two passes. The first gives each category its own
 * colour unless a larger one already holds it; the second gives every
 * category left over (a clash, or an own colour that is slate) the first hue
 * no group holds. Borrowing waits for the first pass to finish, so a borrow
 * never takes the colour a smaller category would have kept.
 *
 * Past nine series the hues repeat; `shadesOf` tells the repeats apart.
 */
export function assignHues(
    series: readonly SeriesIdentity[],
    groupBy: ReportGroupBy,
): Map<string, ChartHue> {
    const out = new Map<string, ChartHue>();
    if (groupBy !== 'Category') {
        series.forEach((s, i) => out.set(s.key, CHART_HUES[i % CHART_HUES.length]!));
        return out;
    }

    const held = new Set<ChartHue>();
    const borrowers: SeriesIdentity[] = [];
    for (const s of series) {
        const own = categoryChipVariant(s.name, 'category', s.groupId, s.isSystemCategory);
        if (isHue(own) && !held.has(own)) {
            out.set(s.key, own);
            held.add(own);
        } else {
            borrowers.push(s);
        }
    }
    borrowers.forEach((s, i) => {
        const free = CHART_HUES.find((h) => !held.has(h));
        const hue = free ?? CHART_HUES[i % CHART_HUES.length]!;
        out.set(s.key, hue);
        held.add(hue);
    });
    return out;
}

/**
 * The shade each series draws in: its hue's first holder takes the family's own
 * colour, the second a deeper one, the third deeper again, and a fourth starts
 * over. Series come largest first, so the biggest keep the plain colours.
 */
export function shadesOf(
    series: readonly SeriesIdentity[],
    hues: ReadonlyMap<string, ChartHue>,
): Map<string, number> {
    const seen = new Map<ChartHue, number>();
    const out = new Map<string, number>();
    for (const s of series) {
        const hue = hues.get(s.key)!;
        const n = seen.get(hue) ?? 0;
        seen.set(hue, n + 1);
        out.set(s.key, n % SHADES);
    }
    return out;
}

/** The key a group is known by: its id, or its name where it has none. */
export const groupKey = (groupId: string | null, groupName: string): string =>
    groupId ?? `name:${groupName}`;

/**
 * Other's label: "Other (N)", or the one group's own name when the fold holds
 * a single group, which is then not "other" anything.
 *
 * The name is the server's `only`, named as any series is, under its whole
 * path when it is a subcategory. It is still the
 * fold, so it keeps Other's slate; only the label changes. Without `only` (a
 * fold of several, or a server that does not send it) the count is shown.
 */
export function otherLabel(rest: Pick<ReportRest, 'count' | 'only'>, groupBy: ReportGroupBy): GroupNameParts {
    const only = rest.only;
    if (only) return groupNameParts(groupBy, only.groupName, only.groupPath);
    return { parents: [], leaf: `Other (${rest.count})` };
}
