import type { ReactNode } from 'react';

import { formatCurrency, formatSignedAmount } from '@/lib/money';
import type { RegisterRowBase } from '@/lib/types';

import { NoRate } from '../../home/Money';

// A register row's two figures, each in its own currency (ADR-0114 D7). On a real
// account both are the account's. On a category, which holds no money of its own, the
// Amount is in the currency of the real account on the other side of the posting, so
// one category register can mix currencies, and the Balance combines them: converted
// into the reporting currency at each transaction's date.

/** The signed Amount, in the currency it was posted in. */
export function rowAmountText(row: Pick<RegisterRowBase, 'amount' | 'currencyCode'>): string {
    return formatSignedAmount(row.amount, row.currencyCode);
}

/**
 * The running Balance, in `balanceCurrencyCode`. A category balance that needs a
 * missing rate is null from that row on, and reads "no rate" with the missing pair and
 * day on hover (ADR-0114 D9). Any other null balance prints `empty`.
 */
export function rowBalance(
    row: Pick<RegisterRowBase, 'balanceAfter' | 'balanceCurrencyCode' | 'balanceRateUnavailable'>,
    empty = '',
): ReactNode {
    if (row.balanceAfter === null) {
        const missing = row.balanceRateUnavailable ?? [];
        return missing.length > 0 ? <NoRate missing={missing} /> : empty;
    }
    return formatCurrency(row.balanceAfter, row.balanceCurrencyCode);
}
