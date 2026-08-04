// Ledger overview aggregate (ADR-0056 slice 1). Mirror of the API
// LedgerOverviewDto — the dashboard's financial summary.

export interface OverviewAccount {
    id: string;
    name: string;
    /** bank | cash | credit_card | investment | asset | liability | loan */
    accountType: string;
    currencyCode: string;
    /** Current balance; liabilities are negative. */
    balance: number;
}

export interface OverviewAccountGroup {
    accountType: string;
    subtotal: number;
    accounts: OverviewAccount[];
}

export interface PortfolioRollup {
    value: number;
    costBasis: number;
    unrealizedGain: number;
    percentChange: number;
}

export interface LedgerOverview {
    netWorth: number;
    totalAssets: number;
    totalLiabilities: number;
    investmentsValue: number;
    currencyCode: string;
    /** Accounts span more than one currency — totals are summed without FX. */
    mixedCurrency: boolean;
    accountGroups: OverviewAccountGroup[];
    portfolio: PortfolioRollup;
}
