import type { AccountSummary } from '@/lib/types';

/**
 * The currency a bank-editor posting's Amount is typed in, and so the symbol its input
 * shows and the places it rounds at (ADR-0114 D14, D15).
 *
 * On a real account's register every posting is in that account's currency: the
 * editor's `currency`. A category holds no money of its own, so on a category's register
 * a posting's Amount is in the currency of the real account on its other side, the
 * account that paid it, and a category leg is checked and rounded at that side's
 * currency (ADR-0114 D15). Picking another paying account moves the figure into that
 * account's currency, so the input follows the pick. A posting between two categories
 * has no paying account and takes the register's category's own code. Until a
 * counterparty is picked, the editor's `currency` stands.
 */
export function postingCurrency(
    counterpartyId: string | null,
    sourceAccountId: string,
    accounts: readonly AccountSummary[],
    currency: string,
): string {
    const source = accounts.find((a) => a.id === sourceAccountId);
    if (source?.accountType !== 'category') return currency;
    const other = counterpartyId === null ? undefined : accounts.find((a) => a.id === counterpartyId);
    if (other === undefined) return currency;
    return other.accountType === 'category' ? source.currencyCode : other.currencyCode;
}
