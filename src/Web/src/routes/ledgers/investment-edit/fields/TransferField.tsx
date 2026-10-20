import { useCallback } from 'react';
import type {
    AccountSummary,
    FrequentCounterpartiesResponse,
} from '@/lib/types';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';

// Account types eligible as transfer source/destination: every
// account type except the current brokerage itself. Categories ARE
// included (ADR-0002: categories are accounts) — MD routinely targets
// a category with an investment transfer leg (e.g. a SellXfr whose
// proceeds book to "Investment Fees"); the server accepts any
// in-ledger active account as the transfer target.
const TRANSFER_ELIGIBLE_TYPES = new Set([
    'bank',
    'credit_card',
    'cash',
    'asset',
    'liability',
    'investment',
    'loan',
    'category',
]);

interface TransferFieldProps {
    /** All accounts in the ledger; the picker filters to eligible
     * transfer targets and excludes the current brokerage. */
    accounts: readonly AccountSummary[];
    /** ADR-0043: most-used counterparties, pinned to the top. */
    frequent?: FrequentCounterpartiesResponse | null;
    /** Excluded from the picker so the user can't pick a
     * self-transfer by accident. */
    brokerageAccountId: string | null;
    valueId: string | null;
    onChangeId: (next: string | null) => void;
    error?: string | null;
    disabled?: boolean;
    /** transfer_shares (ADR-0065): the destination must be another
     * investment account (shares move holdings → holdings). When true
     * the picker shows only investment accounts. */
    restrictToInvestment?: boolean;
}

/**
 * Transfer-destination picker for actions whose layout includes a
 * transfer slot (buyx / sellx / divx / transfer / transfer_shares).
 * For the cash-transfer actions it offers mixed accounts + categories
 * via the shared {@link AccountCategoryPicker} (ADR-0043); for the
 * in-kind transfer_shares it narrows to investment accounts only
 * (shares can only move to a holdings-bearing account). Always
 * excludes the current brokerage.
 */
export function TransferField({
    accounts, frequent, brokerageAccountId, valueId, onChangeId, error, disabled,
    restrictToInvestment = false,
}: TransferFieldProps) {
    const isEligible = useCallback(
        (a: AccountSummary) =>
            a.isActive
            && (restrictToInvestment
                ? a.accountType === 'investment'
                : TRANSFER_ELIGIBLE_TYPES.has(a.accountType))
            && a.id !== brokerageAccountId,
        [brokerageAccountId, restrictToInvestment],
    );
    return (
        <AccountCategoryPicker
            accounts={accounts}
            isEligible={isEligible}
            frequent={frequent}
            valueId={valueId}
            onChangeId={onChangeId}
            label={restrictToInvestment ? 'Transfer shares to' : 'Transfer to / from'}
            placeholder={restrictToInvestment ? 'Investment account…' : 'Account or category…'}
            ariaLabel="Transfer destination"
            error={error}
            disabled={disabled}
        />
    );
}
