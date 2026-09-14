/**
 * sRGB <-> OKLCh, plus gamut probing.
 *
 * WHY NOT HSL. HSL saturation is not perceptually uniform: slate at hue 217 /
 * sat 0.33 reads as a neutral dark grey, while the same 0.33 at hue 62 reads as
 * olive. Rotating a palette's hue in HSL therefore changes how COLOURED it
 * looks, which is the opposite of what a colour direction should do. In OKLCh a
 * constant C is a constant perceived chroma, so a direction can move the hue and
 * hold the character.
 *
 * WHY CONTRAST IS STILL MEASURED AFTERWARDS. OKLCh L is perceptual lightness;
 * WCAG contrast is sRGB relative luminance. Equal L across hues does NOT mean
 * equal contrast — green carries ~72% of luminance and blue ~7%. Every
 * combination has to be checked, which is what `audit()` in palette.ts does.
 */

function srgbToLinear(c: number): number {
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function linearToSrgb(c: number): number {
    const v = c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055;
    return Math.min(1, Math.max(0, v));
}

export interface Oklch {
    L: number;
    C: number;
    H: number;
}

export function hexToOklch(hex: string): Oklch {
    const h = hex.replace('#', '');
    const [r, g, b] = [0, 2, 4].map((i) =>
        srgbToLinear(parseInt(h.slice(i, i + 2), 16) / 255));
    const l = 0.4122214708 * r! + 0.5363325363 * g! + 0.0514459929 * b!;
    const m = 0.2119034982 * r! + 0.6806995451 * g! + 0.1073969566 * b!;
    const s = 0.0883024619 * r! + 0.2817188376 * g! + 0.6299787005 * b!;
    const [l_, m_, s_] = [l, m, s].map((v) => Math.cbrt(v));
    const L = 0.2104542553 * l_! + 0.7936177850 * m_! - 0.0040720468 * s_!;
    const a = 1.9779984951 * l_! - 2.4285922050 * m_! + 0.4505937099 * s_!;
    const bb = 0.0259040371 * l_! + 0.7827717662 * m_! - 0.8086757660 * s_!;
    return {
        L,
        C: Math.hypot(a, bb),
        H: ((Math.atan2(bb, a) * 180) / Math.PI + 360) % 360,
    };
}

export function oklchToHex(L: number, C: number, H: number): string {
    const a = C * Math.cos((H * Math.PI) / 180);
    const bb = C * Math.sin((H * Math.PI) / 180);
    const l_ = L + 0.3963377774 * a + 0.2158037573 * bb;
    const m_ = L - 0.1055613458 * a - 0.0638541728 * bb;
    const s_ = L - 0.0894841775 * a - 1.2914855480 * bb;
    const [l, m, s] = [l_, m_, s_].map((v) => v ** 3);
    const r = 4.0767416621 * l! - 3.3077115913 * m! + 0.2309699292 * s!;
    const g = -1.2684380046 * l! + 2.6097574011 * m! - 0.3413193965 * s!;
    const b = -0.0041960863 * l! - 0.7034186147 * m! + 1.7076147010 * s!;
    return `#${[r, g, b]
        .map((v) => Math.round(linearToSrgb(v) * 255).toString(16).padStart(2, '0'))
        .join('')}`;
}

/**
 * True when the colour survives a round trip — i.e. sRGB can express it.
 * Chroma a hue cannot reach at a given lightness is silently CLIPPED, which is
 * how a direction quietly becomes a different colour. The first cut of this
 * generator fixed accent chroma at 0.13; teal cannot hold that at a dark
 * lightness, every dark candidate clipped, and the light themes' accent landed
 * at 2.66:1 — under AA across the board.
 */
export function inGamut(L: number, C: number, H: number): boolean {
    const round = hexToOklch(oklchToHex(L, C, H));
    return Math.abs(L - round.L) < 0.01 && Math.abs(C - round.C) < 0.01;
}

/** Largest in-gamut chroma at this lightness and hue. */
export function maxChroma(L: number, H: number): number {
    let lo = 0;
    let hi = 0.4;
    for (let i = 0; i < 30; i += 1) {
        const mid = (lo + hi) / 2;
        if (inGamut(L, mid, H)) lo = mid;
        else hi = mid;
    }
    return lo;
}
