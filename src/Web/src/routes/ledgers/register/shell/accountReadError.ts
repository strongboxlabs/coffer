import type { AccountSummary } from '@/lib/types';

/** The message a register shows when its account list answered without it. */
export const ACCOUNT_NOT_LISTED = 'Could not load this account.';

/**
 * The account read's part in a register's list failure. Both registers keep their
 * loading state until the account list has the account, whose currency every
 * figure is in, so that read failing is the list failing too, and so is a list
 * that answered without the account: no currency will come, and the loading
 * state would never end. The reminder dialogs treat the same answer the same way.
 */
export function accountReadError(
    accountsQuery: { isError: boolean; isSuccess: boolean; error: unknown },
    account: AccountSummary | null | undefined,
): unknown {
    if (accountsQuery.isError) return accountsQuery.error;
    if (accountsQuery.isSuccess && !account) return new Error(ACCOUNT_NOT_LISTED);
    return null;
}
