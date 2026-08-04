import { useId } from 'react';
import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { SHARES_SIGN_RULE } from '../actionLayout';
import type { LedgerInvestmentAction } from '@/lib/types';

interface SharesFieldProps {
    /** Current value. Null = empty input. */
    value: number | null;
    onChange: (next: number | null) => void;
    /** The current draft's action — drives the sign-hint helper text
     * and the input's `min` / `max` clamps. */
    action: LedgerInvestmentAction;
    error?: string | null;
    disabled?: boolean;
}

/**
 * Signed shares input. The sign requirement is pinned by the action
 * (sell/sellx must be negative; buy/buyx/divr must be positive — see
 * <c>SHARES_SIGN_RULE</c>); this field accepts any sign at the input
 * level but renders a small hint so the user knows what's expected.
 * Validation (../validation.ts) enforces the sign rule at save time.
 */
export function SharesField({
    value, onChange, action, error, disabled,
}: SharesFieldProps) {
    const inputId = useId();
    const signRule = SHARES_SIGN_RULE[action];
    const signHint = signRule === 'positive'
        ? 'positive (shares acquired)'
        : signRule === 'negative'
            ? 'negative (shares disposed)'
            : null;

    return (
        <div className="flex min-w-0 flex-col gap-1 text-xs">
            <FieldLabel htmlFor={inputId}>Shares</FieldLabel>
            <input
                id={inputId}
                type="number"
                step="0.0001"
                inputMode="decimal"
                value={value ?? ''}
                placeholder="0"
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
