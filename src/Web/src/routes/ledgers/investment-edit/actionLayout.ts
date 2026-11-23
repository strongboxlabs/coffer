import type { AccountSummary, LedgerInvestmentAction } from '@/lib/types';
import { TRANSFER_AMOUNT_ACTIONS } from '@/lib/types/investment';
import type { SecondAmountDirection, SecondAmountSide } from '@/lib/secondAmount';

import { formatInvestmentAction } from '../register/strategies/investmentStrategy';

/**
 * Identifier for one slot in the editor form. Each key corresponds to
 * a field component under `./fields/`. The orchestrator iterates the
 * per-action layout below and renders the corresponding component.
 */
export type FieldKey =
    | 'security'
    | 'shares'
    | 'price'
    | 'amount'
    | 'category'
    | 'transfer'
    | 'fee';

/**
 * The ADR-0029 action × field matrix in data form. Each row lists the
 * field keys that show for that action. The orchestrator iterates this
 * array (no `switch(action)`), so adding a future catalog action is a
 * one-row change and removing/renaming a field is one component edit
 * (referenced from one matrix row).
 *
 * Order in the array IS the order the fields render in the form. The
 * orchestrator wraps them in a flex layout; keep `security` first
 * (anchors the row for buy/sell/etc.) and `fee` last (always optional,
 * separated visually from the required core).
 */
export const ACTION_LAYOUTS: Record<LedgerInvestmentAction, readonly FieldKey[]> = {
    buy:               ['security', 'shares', 'price', 'amount', 'fee'],
    buyx:              ['security', 'shares', 'price', 'amount', 'transfer', 'fee'],
    sell:              ['security', 'shares', 'price', 'amount', 'fee'],
    sellx:             ['security', 'shares', 'price', 'amount', 'transfer', 'fee'],
    dividend_cash:     ['security', 'amount',  'category', 'fee'],
    dividend_reinvest: ['security', 'shares', 'price', 'amount', 'category', 'fee'],
    divx:              ['security', 'amount',  'category', 'transfer', 'fee'],
    transfer:          ['amount',   'transfer'],
    misc:              ['security', 'amount',  'category', 'fee'],
    // In-kind share move (ADR-0065): pick the security, the qty to move,
    // and the destination investment account. No price/amount (unit cost
    // carries per-lot from the source), no fee (no cash moves).
    transfer_shares:   ['security', 'shares', 'transfer'],
} as const;

/**
 * The action set the picker displays in dropdown order. Matches the
 * ADR-0027 catalog order; the editor's action selector iterates this
 * array so the order is data-driven, not hard-coded in JSX.
 *
 * The picker holds only the order and the hints. Each label is the action's
 * one word from `formatInvestmentAction`, the word the register's chip shows,
 * so the picker and the chip cannot name an action differently.
 */
export const ACTION_PICKER_ENTRIES: ReadonlyArray<{
    action: LedgerInvestmentAction;
    label: string;
    /** Short hint shown below the label on focus / hover. */
    hint: string;
}> = ([
    { action: 'buy',
      hint: 'Acquire shares (cash out)' },
    { action: 'buyx',
      hint: 'Acquire shares with cash from another account' },
    { action: 'sell',
      hint: 'Dispose shares (cash in)' },
    { action: 'sellx',
      hint: 'Dispose shares with proceeds to another account' },
    { action: 'dividend_cash',
      hint: 'Cash dividend received' },
    { action: 'dividend_reinvest',
      hint: 'Dividend reinvested into shares' },
    { action: 'divx',
      hint: 'Dividend received then transferred out' },
    { action: 'transfer',
      hint: 'Cash transfer in or out of the brokerage' },
    { action: 'misc',
      hint: 'Other income or expense (sign on amount discriminates)' },
    { action: 'transfer_shares',
      hint: 'Move shares in-kind to another investment account (carries cost basis, no gain)' },
] satisfies ReadonlyArray<{ action: LedgerInvestmentAction; hint: string }>).map(
    (entry) => ({ ...entry, label: formatInvestmentAction(entry.action) }),
);

/**
 * Per-field signed-amount sign requirement, per the field component's
 * input mode. Most fields accept any sign at the wire level; the few
 * that pin a sign (price = positive; shares on sell/sellx = negative)
 * are enforced by the validation function, not by the layout.
 *
 * Kept here as a data table the orchestrator (and validation) can
 * reference without scattering `if (action === 'sell')` checks.
 */
export const SHARES_SIGN_RULE: Record<LedgerInvestmentAction, 'positive' | 'negative' | null> = {
    buy:               'positive',
    buyx:              'positive',
    sell:              'negative',
    sellx:             'negative',
    dividend_cash:     null,        // field hidden
    dividend_reinvest: 'positive',
    divx:              null,        // field hidden
    transfer:          null,        // field hidden
    misc:              null,        // field hidden
    transfer_shares:   'positive',  // qty to move (in-kind)
};

/**
 * The amount field's sign hint when it's user-input (not computed).
 * Drives the helper-text the field component shows below the input.
 */
/**
 * Actions where the editor links price ↔ amount through user-typed
 * shares: amount = shares × price. Editing price recomputes amount and
 * vice-versa; shares stays anchored (never auto-computed). This is also
 * the set whose Amount field is a *derived* value rather than free user
 * input — so a prefill (import or re-open) should seed
 * amount = shares × price, not the net cash flow (which is ~0 for a
 * cash-neutral reinvestment).
 */
export function isLinkedAction(action: LedgerInvestmentAction): boolean {
    return action === 'buy'
        || action === 'sell'
        || action === 'buyx'
        || action === 'sellx'
        || action === 'dividend_reinvest';
}

export const AMOUNT_SIGN_HINT: Record<LedgerInvestmentAction, string | null> = {
    buy:               null,    // computed from shares × price
    buyx:              null,    // nets to zero
    sell:              null,    // computed
    sellx:             null,    // nets to zero
    dividend_cash:     'positive — dividend amount received',
    dividend_reinvest: null,    // nets to zero
    divx:              null,    // nets to zero (income + xfr cancel)
    transfer:          'positive = in, negative = out',
    misc:              'positive = income, negative = expense',
    transfer_shares:   null,    // amount field hidden (in-kind, no cash)
};

/**
 * Which way the money moves for the transfer account, the second amount's caption
 * (ADR-0114 D14): BuyXfr pays from it (Sent); SellXfr and DividendXfr pay into it
 * (Received); an Xfr goes by Amount's sign, money into the brokerage being money the
 * other account sent.
 */
export function transferAmountDirection(
    action: LedgerInvestmentAction,
    amount: number | null,
): SecondAmountDirection {
    switch (action) {
        case 'buyx':
            return 'sent';
        case 'sellx':
        case 'divx':
            return 'received';
        default:
            return amount !== null && amount > 0 ? 'sent' : 'received';
    }
}

/**
 * The second amount's account, while the transfer account is a real account in another
 * currency than the brokerage's on an Xfr, BuyXfr, SellXfr or DividendXfr (ADR-0114 D13,
 * D14); null otherwise: a category, the same currency, an in-kind share move, a
 * currency not yet known. The transfer field's own eligibility is unchanged.
 */
export function transferAmountSide(args: {
    action: LedgerInvestmentAction | null;
    transferAccountId: string | null;
    amount: number | null;
    accounts: readonly AccountSummary[];
    /** The brokerage's currency; null while it is not known. */
    currency: string | null;
    accountName: (id: string) => string;
}): SecondAmountSide | null {
    const { action, transferAccountId, currency } = args;
    if (action === null || !TRANSFER_AMOUNT_ACTIONS.includes(action)) return null;
    if (transferAccountId === null || currency === null) return null;
    const other = args.accounts.find((a) => a.id === transferAccountId);
    if (other === undefined || other.accountType === 'category' || other.currencyCode === currency) return null;
    return {
        accountId: other.id,
        accountName: args.accountName(other.id),
        currency: other.currencyCode,
        direction: transferAmountDirection(action, args.amount),
    };
}
