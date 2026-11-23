// The reads of a ledger's outside market series (ADR-0114), each under one key so the
// screens that show the same list share one query and one cache entry: Settings →
// Quotes manages the benchmarks the Returns report's Benchmark control lists, and the
// rates tab lists the pairs the conversion note links to. A write refreshes the key
// written out as here (HomeReadsRule reads keys only as written-out lists).

import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';

import { fetchBenchmarks, fetchFxRatesView } from '@/lib/api';
import { fetchDerivedRate } from '@/lib/api/marketSeries';

/** A ledger's benchmarks, in the order the Benchmark control lists them.
 *  Key: `['benchmarks', ledgerId]`. */
export function useBenchmarks(ledgerId: string) {
    return useQuery({
        queryKey: ['benchmarks', ledgerId],
        queryFn: () => fetchBenchmarks(ledgerId),
    });
}

/** The pairs a ledger needs and how far each is covered.
 *  Key: `['fx-rates', ledgerId]`, the prefix of each pair's pages too. */
export function useFxRatesView(ledgerId: string) {
    return useQuery({
        queryKey: ['fx-rates', ledgerId],
        queryFn: () => fetchFxRatesView(ledgerId),
    });
}

/** How long the amount must stay unchanged before the derived rate is asked for it. */
export const DERIVED_RATE_DEBOUNCE_MS = 300;

/** What {@link useDerivedRate} asks for. */
export interface DerivedRateArgs {
    ledgerId: string;
    /** The amount's currency; null while it is not known. */
    fromCurrency: string | null;
    /** The second amount's currency; null while it is not known. */
    toCurrency: string | null;
    /** `yyyy-MM-dd`; null while the editor has no date. */
    date: string | null;
    /** The amount to convert, in `fromCurrency`; null for the rate alone. */
    amount: number | null;
    /** False to ask for nothing, as when the second amount is typed or seeded. */
    enabled?: boolean;
}

/**
 * The derived rate between two account currencies for a day, with the amount converted
 * at it (ADR-0114 D13, D14): the prefill and the rate line of a transfer's second amount.
 * Key: `['fx-rates', ledgerId, 'derived', from, to, date, amount]`, under the rates
 * view's prefix, so a hand-entered rate refreshes it.
 *
 * The amount is debounced: while it keeps changing (the user typing) nothing is asked,
 * and once it has been still for {@link DERIVED_RATE_DEBOUNCE_MS} one request goes for
 * the value it settled on. Nothing is asked while a currency or the date is missing, or
 * the two currencies are the same.
 *
 * The key is the live amount, and the debounce only gates the request, so `data` is
 * always the answer for the amount passed now: while the typing has not settled it is
 * undefined (or the cached answer for that same amount), never the previous amount's
 * converted figure.
 */
export function useDerivedRate({ ledgerId, fromCurrency, toCurrency, date, amount, enabled = true }: DerivedRateArgs) {
    const settledAmount = useDebounced(amount, DERIVED_RATE_DEBOUNCE_MS);
    const ready = enabled
        && fromCurrency !== null
        && toCurrency !== null
        && fromCurrency !== toCurrency
        && date !== null
        && settledAmount === amount;
    return useQuery({
        queryKey: ['fx-rates', ledgerId, 'derived', fromCurrency, toCurrency, date, amount],
        queryFn: () => fetchDerivedRate(ledgerId, fromCurrency!, toCurrency!, date!, amount ?? undefined),
        enabled: ready,
    });
}

/** `value` once it has stayed the same for `ms`; the first value at once. */
function useDebounced<T>(value: T, ms: number): T {
    const [settled, setSettled] = useState(value);
    useEffect(() => {
        const id = setTimeout(() => setSettled(value), ms);
        return () => clearTimeout(id);
    }, [value, ms]);
    return settled;
}
