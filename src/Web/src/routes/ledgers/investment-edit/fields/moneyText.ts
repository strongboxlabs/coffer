/**
 * The investment draft holds its figures as numbers: an input's text, as typed or as
 * MoneyInput rounded it on blur, read back as one. Empty text, or text that is not a
 * finite number, is no figure.
 */
export function parseMoneyText(text: string): number | null {
    if (text.length === 0) return null;
    const n = Number(text);
    return Number.isFinite(n) ? n : null;
}
