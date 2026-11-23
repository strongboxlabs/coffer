import { useQuery } from '@tanstack/react-query';

import { fetchAccounts } from '@/lib/api';
import type { AccountSummary } from '@/lib/types';
import { useLedgerRole } from '@/lib/useLedgerRole';

import { ConversionNote, ExchangeRatesLink } from './ConversionNote';

/**
 * Which accounts combine into a screen's figures, and so which accounts' currencies the
 * note names (ADR-0114 D7).
 *
 * - `accounts`: every account that holds money. A category holds none of its own, so its
 *   currency does not count. The Overview, Budget, Reports and Structure → Categories.
 * - `brokerages`: the investment accounts only. The Returns report's ledger line combines
 *   the brokerages and nothing else, so a bank account in another currency converts
 *   nothing there.
 */
export type ConversionScope = 'accounts' | 'brokerages';

const IN_SCOPE: Record<ConversionScope, (a: AccountSummary) => boolean> = {
    accounts: (a) => a.accountType !== 'category',
    brokerages: (a) => a.accountType === 'investment',
};

/**
 * The ONE ledger-level conversion note a page with combined figures carries under its
 * header (ADR-0114 D7). `scope` says which accounts combine on the screen; the default
 * is every account that holds money.
 *
 * It reads only what the screens already share: the reporting currency from the
 * `['ledgers']` read the role comes from, and the account currencies from the rail's
 * accounts read (`['accounts', ledgerId, { includeInactive: true }]`). A closed account
 * still counts towards net worth and towards the ledger's returns line, so it counts
 * here. Nothing is drawn until both reads have answered, or when every account in scope
 * is in the reporting currency.
 */
export function LedgerConversionNote({ ledgerId, scope = 'accounts', className }: {
    ledgerId: string;
    scope?: ConversionScope;
    className?: string;
}) {
    const { ledger } = useLedgerRole(ledgerId);
    const accounts = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
    });
    const reporting = ledger?.currencyCode;
    if (reporting === undefined || accounts.data === undefined) return null;
    return (
        <ConversionNote
            reportingCurrency={reporting}
            currencies={accounts.data.filter(IN_SCOPE[scope]).map((a) => a.currencyCode)}
            ratesLink={<ExchangeRatesLink ledgerId={ledgerId} />}
            className={className}
        />
    );
}
