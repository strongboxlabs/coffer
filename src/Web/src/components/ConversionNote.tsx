import type { ReactNode } from 'react';
import { Link } from '@tanstack/react-router';

import { cn } from '@/lib/cn';
import { conversionNoteText, RATES_LINK_CLASS, rateUnavailableTitle } from '@/lib/conversion';
import type { RateUnavailable } from '@/lib/types';

// Currency conversion on a page (ADR-0114 D7, D9). Converted totals print as plain
// figures; ONE note under the page header says what converts, and a figure with a
// missing rate is marked on itself ("no rate", see NoRate in home/Money.tsx).

/** The link to the ledger's Exchange rates tab, as each note ends with it. */
export function ExchangeRatesLink({ ledgerId }: { ledgerId: string }) {
    return (
        <Link
            to="/ledgers/$ledgerId/settings"
            params={{ ledgerId }}
            search={{ tab: 'rates' }}
            className={RATES_LINK_CLASS}
        >
            Exchange rates →
        </Link>
    );
}

/**
 * The ledger-level note under a page header (Overview, Budget, Reports): totals are
 * in the reporting currency, with the accounts in other currencies converted. Draws
 * nothing on a ledger whose accounts are all in the reporting currency.
 *
 * `currencies` are the ledger's accounts' currencies (repeats and the reporting one
 * included). `ratesLink` is `<ExchangeRatesLink ledgerId={…} />` on a screen.
 */
export function ConversionNote({ reportingCurrency, currencies, ratesLink, className }: {
    reportingCurrency: string;
    currencies: Iterable<string>;
    ratesLink: ReactNode;
    className?: string;
}) {
    const text = conversionNoteText(reportingCurrency, currencies);
    if (text === null) return null;
    return <p className={cn('text-xs text-text-muted', className)}>{text} {ratesLink}</p>;
}

/**
 * The line beside a figure that reads "no rate", where the widget has room to say
 * why: each missing pair and day, then `children` (such as how far the rates are
 * fetched), then the link. Draws nothing when nothing is missing.
 */
export function MissingRateNote({ missing, ratesLink, children, className }: {
    missing: readonly RateUnavailable[];
    ratesLink: ReactNode;
    children?: ReactNode;
    className?: string;
}) {
    if (missing.length === 0) return null;
    return (
        <p className={cn('text-xs text-text-muted', className)}>
            {rateUnavailableTitle(missing)}
            {children ? <> {children}</> : null}
            {' '}
            {ratesLink}
        </p>
    );
}
