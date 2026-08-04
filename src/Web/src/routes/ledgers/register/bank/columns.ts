import type { BankRow } from '@/lib/types';
import { formatLedgerDate } from '@/lib/dates';
import { BANK_COLS } from '../strategies/bankStrategy';

/**
 * Bank-register column template + bank-specific row helpers.
 *
 * `BANK_COLS` is the canonical grid template (defined in
 * `../strategies/bankStrategy`); it's re-exported here so the page,
 * the row components, and the column header strip share one import
 * the same way the investment register shares `INVESTMENT_REGISTER_COLS`.
 *
 * The register-wide status helpers (`resolveRowStatus`, `isScheduled`,
 * `passesStatusFilter`, `StatusFilter`, `RowStatus`) now live in the
 * shared `../shell/registerStatus` module so the bank AND investment
 * registers derive status identically. They're re-exported here so
 * existing bank-side importers (the page + row components) don't churn.
 *
 * The genuinely bank-specific helpers (`taxDateSubLabel`,
 * `isInvestmentOwnedRow`) stay below — they read `BankRow`-only fields.
 * This is a non-component module so the row components can import it
 * without tripping the react-refresh "only export components" rule.
 */
export { BANK_COLS } from '../strategies/bankStrategy';
export {
    isScheduled,
    passesStatusFilter,
    resolveRowStatus,
    type RowStatus,
    type StatusFilter,
} from '../shell/registerStatus';

/** Bank register grid: status / checkbox / date / check# / payee+memo
 *  / category+tags / amount / balance. Alias kept for symmetry with
 *  the investment register's `INVESTMENT_REGISTER_COLS`. */
export const BANK_REGISTER_COLS = BANK_COLS;

/**
 * True when this bank-register row's canonical owner is an investment
 * header (a Buy / Sell / Div / Misc cash leg landing in a bank
 * account — read-only here; edit + delete belong on the brokerage
 * register).
 *
 * Under the account-domain discriminant (ADR-0030 §2) a bank
 * account's rows are all `BankRow`, which carries no
 * `investmentAction`. The signal moves to the universal
 * `derivedAction`: the view defines it as
 * `COALESCE(header.action, 'Xfr' on transfer-shape legs)` (mig 108),
 * so it is non-null-and-not-`'Xfr'` exactly when the owning header
 * carries an investment action — equivalent to the former
 * `investmentAction !== null` test, sourced from a field that
 * survives on bank rows. A plain bank transfer leg reads
 * `derivedAction === 'Xfr'` (the synthesized marker) and is NOT
 * investment-owned; a plain non-transfer bank row reads `null`.
 */
export function isInvestmentOwnedRow(txn: BankRow): boolean {
    return txn.derivedAction !== null && txn.derivedAction !== 'Xfr';
}

/**
 * Returns the short tax-date label to render under the posted date,
 * or `null` when `transactedAt` is missing or falls on the same
 * calendar day as `postedAt`. Same noise-filter MD uses — if the
 * dates match, no second line is shown.
 */
export function taxDateSubLabel(txn: BankRow): string | null {
    if (txn.transactedAt === null) return null;
    // UTC-anchored calendar-date strings — same noise filter MD
    // uses (no sub-label when posted == transacted).
    if (txn.postedAt.slice(0, 10) === txn.transactedAt.slice(0, 10)) return null;
    return formatLedgerDate(txn.transactedAt);
}
