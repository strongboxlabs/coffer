/**
 * Strings both registers must say identically.
 *
 * `selectAllLabel` is the accessible name of the header checkbox, and the two
 * registers disagreed about what it promises: bank said "...matching the
 * current filter", investment said "Select all transactions in this account".
 * Investment's overstated its scope — that register has the same filter bar,
 * so the checkbox selects the FILTERED set there too, and a screen-reader user
 * was told they were about to act on every transaction in the account.
 *
 * Here rather than in each page so the two cannot drift again: the previous
 * arrangement was two literals that merely happened to be near each other.
 */
export const REGISTER_SELECT_ALL_LABEL =
    'Select all transactions in this account matching the current filter';
