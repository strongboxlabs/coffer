import {
    BarChart3,
    Briefcase,
    CreditCard,
    Landmark,
    PiggyBank,
    Wallet,
    type LucideIcon,
} from 'lucide-react';

// Canonical account-type display metadata — the single source of truth for
// how the seven API account types render as grouped sections (label + icon)
// across the app. Mirrors the API's types (bank / cash / credit_card /
// investment / asset / liability / loan); the Ledger Hub and the sidebar's
// "All" account list both group by these so they never drift apart.
//
// `holding` accounts are the system-side sibling of an `investment` account
// and never group on their own — callers fold them into `investment` before
// looking up meta. `category` rows are budget categories, not accounts, and
// are excluded by callers.
export const ACCOUNT_TYPE_META: Record<
    string,
    { label: string; icon: LucideIcon }
> = {
    bank: { label: 'Banking', icon: PiggyBank },
    cash: { label: 'Cash', icon: Wallet },
    credit_card: { label: 'Credit cards', icon: CreditCard },
    investment: { label: 'Investments', icon: BarChart3 },
    asset: { label: 'Assets', icon: Briefcase },
    liability: { label: 'Liabilities', icon: Landmark },
    loan: { label: 'Loans', icon: Landmark },
};

/** Label + icon for an account type. Unknown types fall back to the raw type
 *  string with a generic icon (defensive — the seven above are exhaustive). */
export function accountTypeMeta(accountType: string): {
    label: string;
    icon: LucideIcon;
} {
    return ACCOUNT_TYPE_META[accountType] ?? { label: accountType, icon: Wallet };
}

// Display order for the account-type groups — mirrors
// OverviewRepository.TypeOrder (assets first, then liabilities) so the
// sidebar and Hub present the same sequence.
const TYPE_ORDER: Record<string, number> = {
    bank: 0,
    cash: 1,
    credit_card: 2,
    investment: 3,
    asset: 4,
    liability: 5,
    loan: 6,
};

/** Sort key for an account type; unknown types sort last. */
export function accountTypeOrder(accountType: string): number {
    return TYPE_ORDER[accountType] ?? 99;
}
