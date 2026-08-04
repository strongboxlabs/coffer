import { useId } from 'react';
import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { AMOUNT_SIGN_HINT } from '../actionLayout';
import type { LedgerInvestmentAction } from '@/lib/types';

interface AmountFieldProps {
    /** Current value. Null = empty input. */
    value: number | null;
    onChange: (next: number | null) => void;
    /** Drives the sign-hint helper text shown beneath the input
     * (e.g., "positive = income, negative = expense" for misc). */
    action: LedgerInvestmentAction;
    error?: string | null;
    disabled?: boolean;
}

/**
 * Signed amount input. Used by dividend_cash (positive — dividend
 * value), transfer (sign = direction), and misc (sign = income vs
 * expense). The sign hint comes from <c>AMOUNT_SIGN_HINT</c> in
 * <c>actionLayout</c> so the per-action wording lives next to the
 * matrix, not scattered through JSX.
 */
export function AmountField({
    value, onChange, action, error, disabled,
}: AmountFieldProps) {
    const inputId = useId();
    const signHint = AMOUNT_SIGN_HINT[action];

    return (
        <div className="flex min-w-0 flex-col gap-1 text-xs">
            <FieldLabel htmlFor={inputId}>Amount</FieldLabel>
            <input
                id={inputId}
                type="number"
                step="0.01"
                inputMode="decimal"
                value={value ?? ''}
                placeholder="0.00"
                disabled={disabled}
                onChange={(e) => {
                    const v = e.target.value;
                    if (v.length === 0) {
                        onChange(null);
                    } else {
                        const n = Number(v);
                        onChange(Number.isFinite(n) ? n : null);
                    }
                }}
                className={cn(
                    'h-7 w-full rounded border bg-surface px-2 text-right font-mono text-xs tabular-nums',
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                    error ? 'border-state-danger' : 'border-border',
                )}
                aria-invalid={!!error}
                aria-describedby={error ? `${inputId}-error` : undefined}
            />
            {error ? (
                <span
                    id={`${inputId}-error`}
                    className="text-[0.6875rem] leading-tight text-state-danger"
                >
                    {error}
                </span>
            ) : signHint ? (
                <span className="text-[0.625rem] leading-tight text-text-subtle">
                    {signHint}
                </span>
            ) : null}
        </div>
    );
}
