// Centralized money / share / price formatting for numeric values
// crossing the API ↔ SPA boundary.
//
// Numbers arrive from the API as JavaScript `number`s (decimals are
// JSON-serialized). For DISPLAY we run them through `Intl.NumberFormat`,
// and historically every screen re-declared its own `formatCurrency` /
// `formatPrice` / `formatShares` / `formatQuantity` helper. Those copies
// drifted apart — currency sometimes 2 digits and sometimes min2/max2,
// shares max4 in one place and unbounded elsewhere — so the same value
// could render differently per screen.
//
// The rule: ONE canonical precision per concept, defined here.
//
//   - CURRENCY (account balances, amounts, fees) → exactly 2 fraction
//     digits. `formatCurrency` (sign auto) / `formatSignedAmount`
//     (explicit +/- via signDisplay 'auto', i.e. minus on negatives).
//   - PRICE (per-share / per-unit prices) → minimumFractionDigits 2,
//     maximumFractionDigits 6, so sub-cent unit prices like 31.530001
//     (bond / MMF rates) survive. This is the ONE place where money is
//     allowed more than 2 decimals.
//   - SHARES / QUANTITY → decimal style, minimumFractionDigits 0,
//     maximumFractionDigits 4 (e.g. 1,221.2474). The database holds the
//     full precision; the display rounds. `formatShares` is canonical;
//     `formatQuantity` is an alias kept to minimize call-site churn
//     (both names appeared across the codebase).
//
// All ad-hoc `new Intl.NumberFormat(undefined, { style: 'currency'… })`
// / `formatShares` / `formatPrice` helpers across the codebase should be
// replaced with these. Genuinely unrelated numeric formatters
// (percentages, plain integers) are NOT money and stay local.
//
// Locale follows the OS / browser default (passing `undefined` to
// `Intl.NumberFormat`); only style / currency / precision are pinned.

// `Intl.NumberFormat` construction is comparatively expensive, so — like
// `lib/dates.ts` pins its formatter instances — we memoize one instance
// per (style, currency) shape. Currency formatters are keyed by currency
// code; the share formatter is currency-independent so it's a singleton.

const currencyFormatters = new Map<string, Intl.NumberFormat>();
const signedCurrencyFormatters = new Map<string, Intl.NumberFormat>();
const priceFormatters = new Map<string, Intl.NumberFormat>();

function currencyFormatter(currency: string): Intl.NumberFormat {
    let fmt = currencyFormatters.get(currency);
    if (fmt === undefined) {
        fmt = new Intl.NumberFormat(undefined, {
            style: 'currency',
            currency,
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
        });
        currencyFormatters.set(currency, fmt);
    }
    return fmt;
}

function signedCurrencyFormatter(currency: string): Intl.NumberFormat {
    let fmt = signedCurrencyFormatters.get(currency);
    if (fmt === undefined) {
        // signDisplay 'auto': minus on negatives, nothing on positives.
        // Explicit so a future intl tweak doesn't flip the convention
        // silently (modern-fintech: leading minus, no leading plus).
        fmt = new Intl.NumberFormat(undefined, {
            style: 'currency',
            currency,
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
            signDisplay: 'auto',
        });
        signedCurrencyFormatters.set(currency, fmt);
    }
    return fmt;
}

function priceFormatter(currency: string): Intl.NumberFormat {
    let fmt = priceFormatters.get(currency);
    if (fmt === undefined) {
        fmt = new Intl.NumberFormat(undefined, {
            style: 'currency',
            currency,
            minimumFractionDigits: 2,
            maximumFractionDigits: 6,
        });
        priceFormatters.set(currency, fmt);
    }
    return fmt;
}

const SHARES_FORMAT = new Intl.NumberFormat(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: 4,
});

// JS has a signed zero: negating a zero (e.g. flipping an income total's
// sign, -(0)) yields -0, which Intl renders as "-$0.00" / "-0". No money
// or quantity display ever wants a signed zero, so collapse exact -0 to
// +0 before formatting. (Genuine small negatives like -0.001 are NOT -0,
// so this leaves a real "rounds to -0.00" value's sign intact.)
const noNegZero = (n: number): number => (n === 0 ? 0 : n);

/**
 * Canonical currency formatter — currency style, exactly 2 fraction
 * digits. Use for balances, amounts, fees. Returns `''` for
 * null/undefined/NaN so callers can render a blank cell.
 */
export function formatCurrency(amount: number | null | undefined, currency = 'USD'): string {
    if (amount === null || amount === undefined || Number.isNaN(amount)) return '';
    return currencyFormatter(currency).format(noNegZero(amount));
}

/**
 * Signed currency formatter for single-amount columns. Same precision
 * as `formatCurrency` but with an explicit sign (minus on negatives,
 * nothing on positives — `signDisplay: 'auto'`). Colour is the caller's
 * responsibility; this owns the digits + minus sign.
 */
export function formatSignedAmount(amount: number | null | undefined, currency = 'USD'): string {
    if (amount === null || amount === undefined || Number.isNaN(amount)) return '';
    return signedCurrencyFormatter(currency).format(noNegZero(amount));
}

/**
 * Canonical per-unit price formatter — currency style, min 2 / max 6
 * fraction digits. The one place money is allowed sub-cent precision,
 * for bond / MMF / per-share unit prices like 31.530001.
 */
export function formatPrice(price: number | null | undefined, currency = 'USD'): string {
    if (price === null || price === undefined || Number.isNaN(price)) return '';
    return priceFormatter(currency).format(noNegZero(price));
}

/**
 * Canonical share / quantity formatter — decimal style, min 0 / max 4
 * fraction digits (e.g. 1,221.2474). The database holds full precision;
 * the display rounds.
 */
export function formatShares(qty: number | null | undefined): string {
    if (qty === null || qty === undefined || Number.isNaN(qty)) return '';
    return SHARES_FORMAT.format(noNegZero(qty));
}

/**
 * Alias of {@link formatShares}. Kept because both `formatShares` and
 * `formatQuantity` appeared across the codebase; importing whichever name
 * a call site already used minimizes churn. Identical canonical output.
 */
export const formatQuantity = formatShares;
