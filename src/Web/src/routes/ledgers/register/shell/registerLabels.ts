/**
 * Strings both registers must say identically.
 *
 * `selectAllLabel` is the accessible name of the header checkbox. Both
 * registers have the same filter bar, so the checkbox selects the FILTERED set
 * in both; a label of "Select all transactions in this account" overstates its
 * scope and tells a screen-reader user they are about to act on every
 * transaction in the account.
 *
 * Here rather than in each page so the two cannot drift: two literals that
 * merely sit near each other are free to disagree.
 */
export const REGISTER_SELECT_ALL_LABEL =
    'Select all transactions in this account matching the current filter';
