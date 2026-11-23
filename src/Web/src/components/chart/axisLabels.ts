/**
 * Which period labels a chart draws at a given width, so no label ever overlaps
 * another (AxisLabels).
 *
 * The step is picked by the space each column has, read by a container query on
 * the column itself: nothing here measures. Every period keeps its column; only the
 * TEXT thins. At a spacing of k columns the labels drawn are every k-th from the
 * first, so they stay evenly spaced, each centred under its own column, and the
 * last period's label is drawn too. The spacings form a chain (each divides the
 * next), which is what lets one threshold per label decide it: a label is drawn
 * while its column is wide enough for the widest spacing it belongs to.
 *
 * The last label is drawn while the whole axis has room for it and the first
 * (hideLastBelow, a query on the axis), whatever the spacing. It sits at its column's
 * end rather than past the plot's edge when it is wider than its column, so it can
 * lean up to half its width towards the label before it: a label is drawn only
 * while it is at least one and a half spacings from the last, and the gap before
 * the last is between one and a half and two and a half spacings.
 */

/** The spacings a chart's labels thin through, in columns. */
export const LABEL_SPACINGS = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512] as const;
type Spacing = (typeof LABEL_SPACINGS)[number];

/**
 * For each of `count` labels, the widest spacing at which it is still drawn:
 * Infinity for the first and the last, which every spacing draws (the last while
 * the axis has room for it, hideLastBelow).
 */
export function labelSpacings(count: number): number[] {
    return Array.from({ length: count }, (_, i) => {
        if (i === 0 || i === count - 1) return Infinity;
        // The columns between this label and the last, which it has to clear.
        const room = count - 1 - i;
        let widest = 1;
        for (const k of LABEL_SPACINGS) {
            // Along a chain, a label off one spacing's multiples is off every wider one's.
            if (i % k !== 0 || 1.5 * k > room + 0.5) break;
            widest = k;
        }
        return widest;
    });
}

/**
 * Hides a label while its column is narrower than its text and a one-character
 * gap, divided by the widest spacing it is drawn at: `ch` in a container query is
 * the column's own font's, the labels' font. Period labels are at most ten
 * characters ("yyyy-MM-dd"), so three lengths cover them: a year, a month or a
 * quarter, a day. Written out in full because Tailwind reads class names off the
 * source.
 */
const HIDE_BELOW: Record<4 | 7 | 10, Record<Spacing, string>> = {
    4: {
        1: '@max-[5ch]/period:hidden',
        2: '@max-[2.5ch]/period:hidden',
        4: '@max-[1.25ch]/period:hidden',
        8: '@max-[0.625ch]/period:hidden',
        16: '@max-[0.3125ch]/period:hidden',
        32: '@max-[0.15625ch]/period:hidden',
        64: '@max-[0.078125ch]/period:hidden',
        128: '@max-[0.0390625ch]/period:hidden',
        256: '@max-[0.01953125ch]/period:hidden',
        512: '@max-[0.009765625ch]/period:hidden',
    },
    7: {
        1: '@max-[8ch]/period:hidden',
        2: '@max-[4ch]/period:hidden',
        4: '@max-[2ch]/period:hidden',
        8: '@max-[1ch]/period:hidden',
        16: '@max-[0.5ch]/period:hidden',
        32: '@max-[0.25ch]/period:hidden',
        64: '@max-[0.125ch]/period:hidden',
        128: '@max-[0.0625ch]/period:hidden',
        256: '@max-[0.03125ch]/period:hidden',
        512: '@max-[0.015625ch]/period:hidden',
    },
    10: {
        1: '@max-[11ch]/period:hidden',
        2: '@max-[5.5ch]/period:hidden',
        4: '@max-[2.75ch]/period:hidden',
        8: '@max-[1.375ch]/period:hidden',
        16: '@max-[0.6875ch]/period:hidden',
        32: '@max-[0.34375ch]/period:hidden',
        64: '@max-[0.171875ch]/period:hidden',
        128: '@max-[0.0859375ch]/period:hidden',
        256: '@max-[0.04296875ch]/period:hidden',
        512: '@max-[0.021484375ch]/period:hidden',
    },
};

/** The length a chart's labels are thinned for: the longest of them, rounded up. */
export function labelLength(labels: readonly string[]): 4 | 7 | 10 {
    const longest = Math.max(0, ...labels.map((l) => l.length));
    return longest <= 4 ? 4 : longest <= 7 ? 7 : 10;
}

/** The class that hides a label below its column's threshold; none for the ends. */
export function hideLabelBelow(length: 4 | 7 | 10, widest: number): string {
    if (widest === Infinity) return '';
    return HIDE_BELOW[length][widest as Spacing];
}

/**
 * Hides the last label while the axis is too narrow for it and the first. The first
 * is centred on its column and the last can end at the plot's edge, so with `n`
 * columns of width `c` they clear each other when (n - 0.5)·c is at least one and a
 * half labels and a gap: over the axis's width, n / (n - 0.5) times
 * (1.5·length + 1)ch. That factor falls with n, so the thresholds are taken at the
 * fewest columns of each band (2, 3, 4, 6, 12) and rounded up to a half `ch`.
 */
const LAST_SHOWN: Record<4 | 7 | 10, readonly [string, string, string, string, string]> = {
    4: ['@max-[9.5ch]/axis:hidden', '@max-[8.5ch]/axis:hidden', '@max-[8ch]/axis:hidden', '@max-[8ch]/axis:hidden', '@max-[7.5ch]/axis:hidden'],
    7: ['@max-[15.5ch]/axis:hidden', '@max-[14ch]/axis:hidden', '@max-[13.5ch]/axis:hidden', '@max-[13ch]/axis:hidden', '@max-[12ch]/axis:hidden'],
    10: ['@max-[21.5ch]/axis:hidden', '@max-[19.5ch]/axis:hidden', '@max-[18.5ch]/axis:hidden', '@max-[17.5ch]/axis:hidden', '@max-[17ch]/axis:hidden'],
};

/** The class that hides the last of `count` labels while the axis has no room for it and the first. */
export function hideLastBelow(length: 4 | 7 | 10, count: number): string {
    const band = count <= 2 ? 0 : count === 3 ? 1 : count <= 5 ? 2 : count <= 11 ? 3 : 4;
    return LAST_SHOWN[length][band];
}
