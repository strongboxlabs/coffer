import { useCallback, useId } from 'react';
import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import type {
    AccountSummary,
    FrequentCounterpartiesResponse,
} from '@/lib/types';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';

interface FeeFieldProps {
    /** All accounts; the picker filters to expense-kind categories
     * (Investment Fees, Trading Commission, etc.). */
    accounts: readonly AccountSummary[];
    /** ADR-0043: most-used counterparties, pinned to the top. */
    frequent?: FrequentCounterpartiesResponse | null;
    /** Fee category id; null = no fee. */
    feeAccountId: string | null;
    onChangeFeeAccount: (next: string | null) => void;
    /** Positive number; null = no fee. Paired with `feeAccountId`:
     * the validation function enforces account ⇔ amount together. */
    feeAmount: number | null;
    onChangeFeeAmount: (next: number | null) => void;
    /** Single error string covering the paired-presence rule
     * (rendered under the amount input). */
    error?: string | null;
    disabled?: boolean;
    /** Optional contextual hint (e.g., "Adds to cost basis" when
     * the brokerage's is_trade_commission flag is on). Rendered as
     * helper text under the amount when there's no error. */
    contextualHint?: string | null;
}

/**
 * Combined fee category + fee amount widget. Optional on every
 * action except `transfer` (which doesn't carry a fee per
 * ADR-0027). Layout: category picker on the left (the shared
 * {@link AccountCategoryPicker}, ADR-0043 — expense categories,
 * grouped, frequent-pinned), amount input on the right.
 */
export function FeeField({
    accounts, frequent, feeAccountId, onChangeFeeAccount,
    feeAmount, onChangeFeeAmount, error, disabled, contextualHint,
}: FeeFieldProps) {
    const amountId = useId();
    const isEligible = useCallback(
        (a: AccountSummary) =>
            a.isActive && a.accountType === 'category' && a.categoryKind === 'expense',
        [],
    );
    return (
        <fieldset className="flex min-w-0 flex-col gap-1 text-xs">
            <FieldLabel htmlFor={amountId}>Fee (optional)</FieldLabel>
            <div className="flex min-w-0 items-start gap-2">
                <div className="min-w-0 flex-1">
                    <AccountCategoryPicker
                        accounts={accounts}
                        isEligible={isEligible}
                        frequent={frequent}
                        valueId={feeAccountId}
                        onChangeId={onChangeFeeAccount}
                        placeholder="Fee category…"
                        ariaLabel="Fee category"
                        disabled={disabled}
                    />
                </div>
                <input
                    id={amountId}
                    type="number"
                    step="0.01"
                    min="0"
                    inputMode="decimal"
                    value={feeAmount ?? ''}
                    placeholder="0.00"
                    disabled={disabled}
                    onChange={(e) => {
                        const v = e.target.value;
                        if (v.length === 0) {
                            onChangeFeeAmount(null);
                        } else {
                            const n = Number(v);
                            onChangeFeeAmount(Number.isFinite(n) ? n : null);
                        }
                    }}
                    aria-label="Fee amount"
                    aria-invalid={!!error}
                    aria-describedby={error ? `${amountId}-error` : undefined}
                    className={cn(
                        'h-7 w-24 rounded border bg-surface px-2 text-right font-mono text-xs tabular-nums',
                        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                        error ? 'border-state-danger' : 'border-border',
                    )}
                />
            </div>
            {error ? (
                <span
                    id={`${amountId}-error`}
                    className="text-[0.6875rem] leading-tight text-state-danger"
                >
                    {error}
                </span>
            ) : contextualHint ? (
                <span className="text-[0.625rem] leading-tight text-text-subtle">
                    {contextualHint}
                </span>
            ) : null}
        </fieldset>
    );
}
