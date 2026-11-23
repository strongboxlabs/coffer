// Centralized money / share / price formatting for numeric values
// crossing the API ↔ SPA boundary.
//
// Numbers arrive from the API as JavaScript `number`s (decimals are
// JSON-serialized). For DISPLAY we run them through `Intl.NumberFormat`,
// and per-screen copies of a `formatCurrency` / `formatPrice` /
// `formatShares` / `formatQuantity` helper drift apart — currency sometimes
// 2 digits and sometimes min2/max2, shares max4 in one place and unbounded
// elsewhere — so the same value renders differently per screen.
//
// The rule: ONE canonical precision per concept, defined here.
//
//   - CURRENCY (account balances, amounts, fees) → exactly the currency's
//     own ISO 4217 minor units, 0, 2 or 3 fraction digits, from the shared
//     table `lib/minorUnits` reads (ADR-0114 D15). A stored code the table
//     does not accept prints at 2. `formatCurrency` (sign auto) /
//     `formatSignedAmount` (explicit +/- via signDisplay 'auto', i.e. minus
//     on negatives).
//   - PRICE (per-share / per-unit prices) → at least the currency's places
//     and at most the larger of 6 and those places, so unit prices carrying
//     six decimals (bond / MMF rates) survive. A price is not money: this is
//     the one place a currency figure may carry more places than its currency.
//
// The digits are always passed to `Intl` explicitly. Its own defaults come
// from the browser's CLDR data, which disagrees with ISO 4217 for several
// currencies (ADR-0114 D15).
//   - SHARES / QUANTITY → decimal style, minimumFractionDigits 0,
//     maximumFractionDigits 4 (grouped digits, at most four decimals). The database holds the
//     full precision; the display rounds. `formatShares` is canonical;
//     `formatQuantity` is an alias kept to minimize call-site churn
//     (both names appeared across the codebase).
//
// Every currency formatter lives here (ADR-0114 D14): an ad-hoc
// `new Intl.NumberFormat(…, { style: 'currency'… })` elsewhere is refused by
// src/styles/moneyPlaces.test.ts, as is a money figure rounded or padded at
// a fixed two places. Genuinely unrelated numeric formatters (percentages,
// plain integers) are NOT money and stay local.
//
// The money input helpers (rounding, step, placeholder, seed text and the
// currency's symbol inside the input) are at the end of this file.
//
// Locale follows the OS / browser default (passing `undefined` to
// `Intl.NumberFormat`); only style / currency / precision are pinned.

// `Intl.NumberFormat` construction is comparatively expensive, so — like
// `lib/dates.ts` pins its formatter instances — we memoize one instance
// per (style, currency) shape. Currency formatters are keyed by currency
// code; the share formatter is currency-independent so it's a singleton.

import { minorUnits } from './minorUnits';

const currencyFormatters = new Map<string, Intl.NumberFormat>();
const signedCurrencyFormatters = new Map<string, Intl.NumberFormat>();
const priceFormatters = new Map<string, Intl.NumberFormat>();

/** The most places a price prints at, unless its currency has more. */
const PRICE_MAX_PLACES = 6;

function pricePlaces(currency: string): { min: number; max: number } {
    const places = minorUnits(currency);
    return { min: places, max: Math.max(PRICE_MAX_PLACES, places) };
}

function currencyFormatter(currency: string): Intl.NumberFormat {
    let fmt = currencyFormatters.get(currency);
    if (fmt === undefined) {
        const places = minorUnits(currency);
        fmt = new Intl.NumberFormat(undefined, {
            style: 'currency',
            currency,
            minimumFractionDigits: places,
            maximumFractionDigits: places,
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
        const places = minorUnits(currency);
        fmt = new Intl.NumberFormat(undefined, {
            style: 'currency',
            currency,
            minimumFractionDigits: places,
            maximumFractionDigits: places,
            signDisplay: 'auto',
        });
        signedCurrencyFormatters.set(currency, fmt);
    }
    return fmt;
}

function priceFormatter(currency: string): Intl.NumberFormat {
    let fmt = priceFormatters.get(currency);
    if (fmt === undefined) {
        const { min, max } = pricePlaces(currency);
        fmt = new Intl.NumberFormat(undefined, {
            style: 'currency',
            currency,
            minimumFractionDigits: min,
            maximumFractionDigits: max,
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
// sign, -(0)) yields -0, which Intl renders as a zero with a minus sign,
// in both the currency and the plain form. No money
// or quantity display ever wants a signed zero, so collapse exact -0 to
// +0 before formatting. (A genuine negative smaller than one minor unit is NOT -0,
// so this leaves the sign of a real value that rounds to a negative zero intact.)
const noNegZero = (n: number): number => (n === 0 ? 0 : n);

/**
 * Canonical currency formatter — currency style, exactly the currency's
 * minor units as fraction digits ({@link minorUnits}, ADR-0114 D15). Use for
 * balances, amounts, fees. Returns `''` for
 * null/undefined/NaN so callers can render a blank cell.
 *
 * Every formatter here takes the currency as a required argument: nothing is
 * converted (FU-106), so a figure printed in a currency its caller did not name
 * is a wrong figure. A caller that does not know the currency yet prints nothing.
 */
export function formatCurrency(amount: number | null | undefined, currency: string): string {
    if (amount === null || amount === undefined || Number.isNaN(amount)) return '';
    return currencyFormatter(currency).format(noNegZero(amount));
}

/**
 * `amount` at the precision {@link formatCurrency} prints it, so a sign or a
 * comparison read off it agrees with the figure on screen: a residue smaller than
 * the last printed digit is zero, never a negative that prints as a zero. Rounds
 * half away from zero, as the formatter does.
 */
export function atDisplayPrecision(amount: number, currency: string): number {
    const scale = 10 ** minorUnits(currency);
    return noNegZero((Math.sign(amount) * Math.round(Math.abs(amount) * scale)) / scale);
}

/**
 * The symbol `formatCurrency` prints for a currency, for a label too short for a
 * whole figure (a chart axis). The code itself when the locale has no symbol.
 */
export function currencySymbol(currency: string): string {
    return currencyFormatter(currency).formatToParts(0).find((p) => p.type === 'currency')?.value ?? currency;
}

/**
 * Signed currency formatter for single-amount columns. Same precision
 * as `formatCurrency` but with an explicit sign (minus on negatives,
 * nothing on positives — `signDisplay: 'auto'`). Colour is the caller's
 * responsibility; this owns the digits + minus sign.
 */
export function formatSignedAmount(amount: number | null | undefined, currency: string): string {
    if (amount === null || amount === undefined || Number.isNaN(amount)) return '';
    return signedCurrencyFormatter(currency).format(noNegZero(amount));
}

/**
 * Canonical per-unit price formatter — currency style, at least the
 * currency's minor units and at most the larger of 6 and those places
 * (ADR-0114 D15). A price is not money, so it may carry more places than its
 * currency, for bond / MMF / per-share unit prices that carry six decimals.
 */
export function formatPrice(price: number | null | undefined, currency: string): string {
    if (price === null || price === undefined || Number.isNaN(price)) return '';
    return priceFormatter(currency).format(noNegZero(price));
}

/**
 * Canonical share / quantity formatter — decimal style, min 0 / max 4
 * fraction digits (grouped digits, at most four decimals). The database holds full precision;
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

const FX_RATE_FORMAT = new Intl.NumberFormat(undefined, {
    minimumFractionDigits: 4,
    maximumFractionDigits: 6,
});

/**
 * Canonical exchange-rate formatter (ADR-0114 D10): one unit of `from` in `to`, as
 * `1 X = r Y`, at min 4 / max 6 fraction digits. The database holds 12; a
 * rate is a ratio, not money, so it carries no currency style of its own. Returns
 * `''` for null/undefined/NaN, as the other formatters do.
 */
export function formatFxRate(rate: number | null | undefined, from: string, to: string): string {
    if (rate === null || rate === undefined || Number.isNaN(rate)) return '';
    return `1 ${from} = ${FX_RATE_FORMAT.format(rate)} ${to}`;
}

const wholeCurrencyFormatters = new Map<string, Intl.NumberFormat>();

/**
 * A currency figure in whole units, for a label with no room for the minor units (a
 * chart's end label); the table beside it keeps them. Rounds half away from zero, as
 * the other formatters do.
 */
export function formatCurrencyWhole(amount: number, currency: string): string {
    let fmt = wholeCurrencyFormatters.get(currency);
    if (fmt === undefined) {
        fmt = new Intl.NumberFormat(undefined, {
            style: 'currency',
            currency,
            minimumFractionDigits: 0,
            maximumFractionDigits: 0,
        });
        wholeCurrencyFormatters.set(currency, fmt);
    }
    return fmt.format(noNegZero(amount));
}

// ---------------------------------------------------------------------------
// Rounding and money inputs (ADR-0114 D14, D15)
//
// Every money input's step, placeholder and rounding on blur follow its currency's
// places, and the save body is rounded the same way, through the one helper below.
// While the currency is null (the accounts are still loading) nothing is rounded and
// nothing defaults: no symbol, any step.
// ---------------------------------------------------------------------------

/** The places money in `currency` takes, or null while the currency is not known. */
function placesOf(currency: string | null | undefined): number | null {
    return currency ? minorUnits(currency) : null;
}

/**
 * The magnitude of `n` as its shortest round-trip decimal (the text `String(n)` gives),
 * written out without an exponent, split at the decimal point. Rounding works on these
 * digits, not on the binary double, so a midpoint the double sits just under (a figure
 * whose last kept digit is followed by a single five) still rounds away from zero.
 */
function decimalDigits(n: number): [whole: string, fraction: string] {
    const text = String(Math.abs(n));
    const e = text.indexOf('e');
    if (e < 0) {
        const dot = text.indexOf('.');
        return dot < 0 ? [text, ''] : [text.slice(0, dot), text.slice(dot + 1)];
    }
    const mantissa = text.slice(0, e);
    const exponent = Number(text.slice(e + 1));
    const dot = mantissa.indexOf('.');
    const digits = dot < 0 ? mantissa : mantissa.slice(0, dot) + mantissa.slice(dot + 1);
    const point = (dot < 0 ? mantissa.length : dot) + exponent;
    if (point <= 0) return ['0', '0'.repeat(-point) + digits];
    if (point >= digits.length) return [digits + '0'.repeat(point - digits.length), ''];
    return [digits.slice(0, point), digits.slice(point)];
}

/**
 * `value` rounded to its currency's minor units, half away from zero (ADR-0114 D15): the
 * one rounding every money input's blur and every save body uses. A currency that is
 * null rounds nothing, so a figure is never rounded at a guessed precision. A stored
 * code the table does not accept rounds at 2, as it is shown.
 */
export function roundToMinorUnits(value: number, currency: string | null | undefined): number {
    const places = placesOf(currency);
    if (places === null || !Number.isFinite(value)) return value;
    const [whole, fraction] = decimalDigits(value);
    if (fraction.length <= places) return value;
    const up = fraction.charAt(places) >= '5' ? 1n : 0n;
    const kept = (BigInt(whole + fraction.slice(0, places)) + up).toString().padStart(places + 1, '0');
    const magnitude = Number(places === 0 ? kept : `${kept.slice(0, -places)}.${kept.slice(-places)}`);
    return noNegZero(value < 0 ? -magnitude : magnitude);
}

/** A money input's `step`: one minor unit of the currency, or any step while it is null. */
export function amountStep(currency: string | null | undefined): string {
    const places = placesOf(currency);
    if (places === null) return 'any';
    return places === 0 ? '1' : `0.${'0'.repeat(places - 1)}1`;
}

/** A money input's placeholder: a zero at the currency's places, nothing while it is null. */
export function amountPlaceholder(currency: string | null | undefined): string {
    const places = placesOf(currency);
    if (places === null) return '';
    return places === 0 ? '0' : `0.${'0'.repeat(places)}`;
}

/** A money input's `inputMode`: whole units take the numeric keypad, every other currency a decimal one. */
export function amountInputMode(currency: string | null | undefined): 'numeric' | 'decimal' {
    return placesOf(currency) === 0 ? 'numeric' : 'decimal';
}

/**
 * The text a money input is seeded with for a stored figure: at least the currency's
 * places, so a figure shows its trailing zeros, and every digit a stored figure carries
 * beyond them. A stored amount that does not fit its currency is kept as it is (ADR-0114
 * D15), so seeding never rounds it. `''` for no figure. A null currency pads nothing.
 */
export function amountText(value: number | null | undefined, currency: string | null | undefined): string {
    if (value === null || value === undefined || !Number.isFinite(value)) return '';
    const [whole, digits] = decimalDigits(value);
    const places = placesOf(currency) ?? 0;
    const fraction = digits.padEnd(places, '0');
    const sign = value < 0 ? '-' : '';
    return fraction.length > 0 ? `${sign}${whole}.${fraction}` : `${sign}${whole}`;
}

/** Where a money input shows its currency's symbol, and the symbol itself (ADR-0114 D14). */
export interface CurrencyAffix {
    readonly symbol: string;
    /** Before or after the figure, where the currency formatter puts the symbol. */
    readonly position: 'before' | 'after';
}

/**
 * The symbol a money input shows for `currency`, muted and not typed, and which side of
 * the figure it sits on: both from the formatter `formatCurrency` uses, so the input
 * reads as the displayed amount does and CA$ is told apart from $ (ADR-0114 D14). Null
 * for a null currency, and for a code `Intl` cannot format (one still being typed):
 * no symbol, never a default. `locale` is the browser's when omitted.
 */
export function currencyAffix(currency: string | null | undefined, locale?: string): CurrencyAffix | null {
    if (!currency) return null;
    let parts: Intl.NumberFormatPart[];
    try {
        const fmt = locale === undefined
            ? currencyFormatter(currency)
            : new Intl.NumberFormat(locale, {
                style: 'currency',
                currency,
                minimumFractionDigits: minorUnits(currency),
                maximumFractionDigits: minorUnits(currency),
            });
        parts = fmt.formatToParts(1);
    } catch {
        return null;
    }
    const at = parts.findIndex((p) => p.type === 'currency');
    const figure = parts.findIndex((p) => p.type === 'integer');
    if (at < 0 || figure < 0) return null;
    return { symbol: parts[at]!.value, position: at < figure ? 'before' : 'after' };
}

const AS_SENT = /^([+-]?)(\d+)(?:\.(\d+))?$/;

/**
 * A bank's figure exactly as it sent it (ADR-0114 D15, the Review marker): every digit,
 * a trailing zero and the sign kept, grouped and pointed for the locale, with no
 * currency symbol, so it is not read as an amount in the account's currency. It takes
 * the figure as the API's exact text, because a JSON number would lose a trailing zero.
 * Intl formats that exact decimal string at its own fraction length, so the sign, every
 * digit and the point come out in the locale's one numbering system. Text that is not a
 * plain decimal is returned as it came. `locale` is the browser's when omitted.
 */
export function formatAsSent(text: string, locale?: string): string {
    const exact = text.trim();
    const match = AS_SENT.exec(exact);
    if (match === null) return text;
    const [, sign = '', , fraction] = match;
    const places = fraction?.length ?? 0;
    try {
        const fmt = new Intl.NumberFormat(locale, {
            minimumFractionDigits: places,
            maximumFractionDigits: places,
            signDisplay: sign === '+' ? 'always' : 'auto',
        });
        // Intl takes a decimal string as an exact value; the ES2022 lib types format() as
        // number | bigint only, so the string is passed through a widened signature.
        return (fmt as unknown as { format(value: string): string }).format(exact);
    } catch {
        return text;
    }
}
