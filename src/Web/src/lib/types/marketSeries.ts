// Outside market series (ADR-0114): a ledger's benchmarks and the exchange rates its
// figures convert at. Mirrors of the API MarketSeriesDtos.

import type { RateUnavailable } from './conversion';

/**
 * One of a ledger's benchmarks (ADR-0114 D1): up to three, in the order the Returns
 * report's Benchmark control lists them.
 */
export interface Benchmark {
    id: string;
    /** The Yahoo symbol, upper-cased as it was checked. */
    symbol: string;
    /** The fund's name as Yahoo gave it when added, else the symbol. */
    name: string;
    /** The currency its closes are quoted in, major unit. */
    currencyCode: string;
    /** 1-based order; compact (1..n). */
    position: number;
    /** Whether the Returns report can draw it, for the reader (ADR-0114 D1). Set on the
     *  list; null on an add's answer. */
    availability: BenchmarkAvailability | null;
}

/** Why no point of a benchmark can be drawn: no history stored yet, and the reader's
 *  Yahoo opt-in is off on this ledger (`yahoo-off`) or on (`no-history`). */
export type BenchmarkUnavailableReason = 'yahoo-off' | 'no-history';

/** What stands between a benchmark and its line, as the server knows it. */
export interface BenchmarkAvailability {
    /** Null when its history is stored. */
    unavailableReason: BenchmarkUnavailableReason | null;
    /** The series currencies (reporting and each brokerage's), other than its own, the
     *  ledger holds no rate into from its currency: against a series in one of these,
     *  every point is "rate unavailable" (ADR-0114 D9). */
    noRatesInto: string[];
}

/** Why the ledger needs a pair: a price currency into its holding account's
 *  (`position`), an account's currency into the reporting currency (`net-worth`), or a
 *  benchmark's currency into a returns series' (`benchmark`). */
export type FxPairNeed = 'position' | 'net-worth' | 'benchmark';

/** One needed pair: one unit of `fromCurrency` in `toCurrency`. Days are `yyyy-MM-dd`. */
export interface FxPair {
    fromCurrency: string;
    toCurrency: string;
    neededFor: FxPairNeed[];
    /** The earliest day a figure needs; null when only the latest rate is needed. */
    neededFrom: string | null;
    /** The first day fetches have asked for; null when never fetched. */
    fetchedFromDate: string | null;
    /** The last day fetches have asked for; null when never fetched. */
    fetchedThroughDate: string | null;
    /** The latest hand-entered rate's day; null when none. */
    latestManualDate: string | null;
    /** The pair's coverage (ADR-0114 D9): the later of the two above. A day after it
     *  has no rate, whatever is stored. */
    coveredThroughDate: string | null;
    /** How many days are stored for the pair, from any source. */
    rateCount: number;
    /** The first stored rate's day, from any source; null when none is stored. */
    firstRateDate: string | null;
    /** The days, from `neededFrom` (today when null) through the server's UTC today, a
     *  conversion of the pair reads "rate unavailable" for (ADR-0114 D9), as spans,
     *  earliest first; empty when none is missing. */
    missingDays: FxMissingSpan[];
    /** What needs the pair, named: accounts first, then securities, then benchmarks. */
    neededBy: FxPairSource[];
}

/** Consecutive days with no rate, `fromDate` through `throughDate`, both included. */
export interface FxMissingSpan {
    fromDate: string;
    throughDate: string;
}

/** One thing that needs a pair: an account (`net-worth`), a security held in an
 *  account (`position`), or a benchmark (`benchmark`). */
export interface FxPairSource {
    neededFor: FxPairNeed;
    /** The account's, the security's or the benchmark's id. */
    id: string;
    name: string;
    /** A security's ticker or a benchmark's symbol. */
    symbol: string | null;
    /** For a security: the account that holds it. */
    accountId: string | null;
    accountName: string | null;
}

/** The rates view (ADR-0114 D10): the pairs the ledger needs and how far each is covered. */
export interface FxRatesView {
    reportingCurrency: string;
    pairs: FxPair[];
}

/** Where a stored rate came from. */
export type FxRateSource = 'frankfurter' | 'manual';

/** One stored rate. */
export interface FxRate {
    /** `yyyy-MM-dd`. */
    date: string;
    rate: number;
    source: FxRateSource;
    /** The source Frankfurter named for a fetched rate. */
    upstream: string | null;
    /** Who typed a manual rate. */
    enteredByUserId: string | null;
}

/** A page of one pair's stored rates, newest first. Pass `nextBefore` as `before` for
 *  the next page; null on the last. */
export interface FxRatePage {
    fromCurrency: string;
    toCurrency: string;
    rates: FxRate[];
    nextBefore: string | null;
}

/** Body of a hand-entered rate (ADR-0114 D10): one unit of `fromCurrency` in
 *  `toCurrency` on `date` (`yyyy-MM-dd`). */
export interface AddFxRateRequest {
    fromCurrency: string;
    toCurrency: string;
    date: string;
    rate: number;
}

/**
 * The rate between two account currencies for a day, derived through the reporting
 * currency R (ADR-0114 D13, D14): what an editor prefills a transfer's second amount
 * from and what its rate line says. Mirror of API `DerivedRateDto`, the answer of
 * `GET /api/ledgers/{ledgerId}/fx-rates/{from}/{to}/derived?date=&amount=`.
 *
 * When R is `toCurrency` it is the stored `fromCurrency`→R rate, when R is
 * `fromCurrency` the inverse of the stored `toCurrency`→R rate, otherwise the cross of
 * the two. Show `converted`, never `amount × rate`: the server computes it at full
 * precision and rounds it once at the destination's places, which a JS number cannot.
 */
export interface DerivedRate {
    fromCurrency: string;
    toCurrency: string;
    /** The day asked for, `yyyy-MM-dd`. */
    date: string;
    /** R, the currency the rate is derived through. */
    reportingCurrency: string;
    /** One unit of `fromCurrency` in `toCurrency`; null when a stored leg is missing. */
    rate: number | null;
    /** The older of the stored legs' days, the day the rate line names; null exactly
     *  when `rate` is. */
    rateDate: string | null;
    /** The stored legs read (account currency into R), each with its own day: one when
     *  R is either currency, two otherwise; empty when `rate` is null. */
    legs: DerivedRateLeg[];
    /** The request's amount at `rate`, rounded once at `toCurrency`'s places, half away
     *  from zero, with the amount's sign; null with no amount or no rate. */
    converted: number | null;
    /** Each missing stored leg (account currency into R) and the day; empty when the
     *  rate is there. The line then says "No A to R rate for the day". */
    rateUnavailable: RateUnavailable[];
}

/** One stored rate a {@link DerivedRate} was derived from: one unit of `fromCurrency`
 *  in `toCurrency` (the reporting currency), as stored for `date`. */
export interface DerivedRateLeg {
    fromCurrency: string;
    toCurrency: string;
    /** `yyyy-MM-dd`. */
    date: string;
    rate: number;
}
