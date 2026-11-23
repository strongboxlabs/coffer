// Currency conversion (ADR-0114 D7, D9). Mirror of the API RateUnavailable record.

/**
 * Why a converted figure is null: it needed the rate from `fromCurrency` into
 * `toCurrency` for `date`, and the ledger holds no such rate. A missing rate fails
 * closed: the figure is null, never a partial sum, and no account is left out of a
 * total.
 *
 * Every figure that can be null for want of a rate carries a list of these. Some
 * surfaces always send the list (empty when nothing is missing); others leave the
 * field out when nothing is missing, so their types mark it optional.
 */
export interface RateUnavailable {
    fromCurrency: string;
    toCurrency: string;
    /** The day the rate was needed for, `yyyy-MM-dd`. */
    date: string;
    /** Always `rate_unavailable`. */
    reason: 'rate_unavailable';
}
