import { useQuery } from '@tanstack/react-query';
import { useParams } from '@tanstack/react-router';

import { fetchAccounts } from '@/lib/api';
import type { AccountSummary } from '@/lib/types';

import { BankRegisterPage } from './bank/BankRegisterPage';
import { InvestmentRegisterPage } from './investment/InvestmentRegisterPage';

/**
 * Thin dispatcher that picks the right register page by account
 * type (ADR-0030 §3). Investment accounts get the dedicated
 * <c>InvestmentRegisterPage</c>; every other account type
 * (bank / credit / cash / asset / liability / loan / category)
 * uses <c>BankRegisterPage</c>.
 */
export function RegisterRouter() {
    const { ledgerId, accountId } = useParams({ strict: false }) as {
        ledgerId: string;
        accountId: string;
    };

    // Fetch the full account universe (incl. inactive) so routing resolves
    // ANY account — an inactive investment account must still reach the
    // investment register, not fall through to the bank one. Same
    // includeInactive key the register pages + the sidebar's "show inactive"
    // query use, so they share one cache entry (no extra fetch).
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
        staleTime: 60_000,
    });

    const accounts: readonly AccountSummary[] = accountsQuery.data ?? [];
    const account = accounts.find((a) => a.id === accountId) ?? null;

    // `key={accountId}` forces a fresh mount per account. Navigating between
    // two same-domain accounts (bank→bank, investment→investment) keeps the
    // same component type at this position, so without a key React would
    // RE-USE the instance and its per-account state would leak across the
    // switch — most visibly the windowed-register seek anchor (a date-timeline
    // jump in account A would then load account B anchored on A's header, which
    // doesn't exist in B → empty register). Remounting resets the whole page
    // (window, anchor, selection, editing) cleanly — each account is its own
    // register view.
    if (account?.accountType === 'investment') {
        return <InvestmentRegisterPage key={accountId} />;
    }
    return <BankRegisterPage key={accountId} />;
}
