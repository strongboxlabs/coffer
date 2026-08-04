import { useCallback } from 'react';
import type {
    AccountSummary,
    FrequentCounterpartiesResponse,
    LedgerInvestmentAction,
} from '@/lib/types';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';

interface CategoryFieldProps {
    /** All accounts in the ledger; the picker filters to the
     * `category` type and an action-appropriate `category_kind`. */
    accounts: readonly AccountSummary[];
    /** ADR-0043: most-used counterparties, pinned to the top. */
    frequent?: FrequentCounterpartiesResponse | null;
    /** The current action — drives which category-kinds are
     * eligible. */
    action: LedgerInvestmentAction;
    valueId: string | null;
    onChangeId: (next: string | null) => void;
    error?: string | null;
    disabled?: boolean;
}

/**
 * Category picker for actions whose layout includes a category
 * slot (dividend_cash / dividend_reinvest / divx / misc). Categories
 * only; the category-kind narrows per action:
 *
 *   - dividend_cash / dividend_reinvest / divx → income kinds.
 *   - misc → income OR expense (the amount sign discriminates
 *     direction per ADR-0027).
 *
 * Built on the shared {@link AccountCategoryPicker} (ADR-0043) — all
 * matches, grouped, id-resolved, with the frequent pin.
 */
export function CategoryField({
    accounts, frequent, action, valueId, onChangeId, error, disabled,
}: CategoryFieldProps) {
    const allowsExpense = action === 'misc';
    const isEligible = useCallback(
        (a: AccountSummary) =>
            a.isActive
            && a.accountType === 'category'
            && (a.categoryKind === 'income'
                || (allowsExpense && a.categoryKind === 'expense')),
        [allowsExpense],
    );
    return (
        <AccountCategoryPicker
            accounts={accounts}
            isEligible={isEligible}
            frequent={frequent}
            valueId={valueId}
            onChangeId={onChangeId}
            label="Category"
            placeholder="Pick a category…"
            ariaLabel="Category"
            error={error}
            disabled={disabled}
        />
    );
}
