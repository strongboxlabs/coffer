/**
 * WCAG relative luminance and contrast ratio.
 *
 * Lives here rather than inside the theme generator because two very different
 * callers need it: the generator (build time, solving token values) and
 * `tagPalette` (run time, picking a foreground for a user-chosen tag colour).
 * Importing the generator into app code would pull the whole OKLCh model into
 * the bundle for the sake of one formula.
 */

function channel(c: number): number {
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

/** WCAG relative luminance of an `#rrggbb` colour. */
export function luminance(hex: string): number {
    const h = hex.replace('#', '');
    const [r, g, b] = [0, 2, 4].map((i) => channel(parseInt(h.slice(i, i + 2), 16) / 255));
    return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
}

/** WCAG contrast ratio, 1..21. Order-independent. */
export function contrastRatio(a: string, b: string): number {
    const [x, y] = [luminance(a), luminance(b)];
    return (Math.max(x!, y!) + 0.05) / (Math.min(x!, y!) + 0.05);
}

/** WCAG AA for normal text. 10-11px labels are still "normal": the 3:1
 *  large-text allowance starts at 18.66px bold / 24px regular. */
export const AA = 4.5;

/**
 * Black or white, whichever contrasts more with `background`.
 *
 * PURE black and white, not near-black/near-white. That is not a stylistic
 * choice — it is what makes the result provably legible. Optimal black/white
 * bottoms out at ~4.58:1 at the luminance crossover, so EVERY colour clears AA;
 * softening the pair to #0b0b0b/#fafafa drops the worst case to 4.34 and puts
 * 197 of an 11,232-colour sweep below the line.
 */
export function readableOn(background: string): '#000000' | '#ffffff' {
    return contrastRatio('#000000', background) >= contrastRatio('#ffffff', background)
        ? '#000000'
        : '#ffffff';
}
