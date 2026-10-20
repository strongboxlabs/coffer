import { useCallback } from 'react';
import type { AccountSummary, FrequentCounterpartiesResponse } from '@/lib/types';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';

interface CategoryFieldProps {
    /** All accounts in the ledger; the picker filters to the `category` type
     *  and to active rows. Kind is DISCLOSED, not filtered — see below. */
    accounts: readonly AccountSummary[];
    /** ADR-0043: most-used counterparties, pinned to the top. */
    frequent?: FrequentCounterpartiesResponse | null;
    valueId: string | null;
    onChangeId: (next: string | null) => void;
    error?: string | null;
    disabled?: boolean;
}

/**
 * Category picker for actions whose layout includes a category slot
 * (dividend_cash / dividend_reinvest / divx / misc).
 *
 * KIND IS NO LONGER A FILTER (ADR-0017). This used to offer income kinds only,
 * widening to expense for `misc` alone. Three reasons that went:
 *
 *   1. It blocked real work. A valuation adjustment — reconciling a retirement
 *      balance, one of the two cases the 'adjustment' kind exists for — could
 *      never be selected here at all, so the feature had no path to the screen.
 *   2. It was not load-bearing. What classifies an investment posting is
 *      `posting_role`, stamped from the header action, NOT category_kind:
 *      investment_income filters on posting_role='income'. Opening this up
 *      cannot corrupt investment income or fee netting.
 *   3. It protected almost nothing. The distortion it nominally guarded against
 *      — money landing in the wrong reporting measure — arrives overwhelmingly
 *      through the IMPORTER, which bypasses every picker.
 *
 * What replaces it is disclosure: the picker states each category's kind on the
 * row, so the choice is informed rather than prevented. `isActive` and
 * `accountType` stay — those are different rules, about archived rows and about
 * what a category IS.
 *
 * Built on the shared {@link AccountCategoryPicker} (ADR-0043) — all
 * matches, grouped, id-resolved, with the frequent pin.
 */
export function CategoryField({
    accounts, frequent, valueId, onChangeId, error, disabled,
}: CategoryFieldProps) {
    const isEligible = useCallback(
        (a: AccountSummary) => a.isActive && a.accountType === 'category',
        [],
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
