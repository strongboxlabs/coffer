import { useId } from 'react';
import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { MoneyInput } from '@/components/ui/MoneyInput';
import { useFieldSizeClass } from '@/components/ui/fieldSize';
import { AMOUNT_SIGN_HINT } from '../actionLayout';
import { parseMoneyText } from './moneyText';
import type { LedgerInvestmentAction } from '@/lib/types';

interface AmountFieldProps {
    /** Current value. Null = empty input. */
    value: number | null;
    onChange: (next: number | null) => void;
    /** The brokerage's currency: the symbol, step and rounding of the input (ADR-0114
     *  D14, D15). Null while the accounts load: no symbol, nothing rounded. */
    currency: string | null;
    /** Drives the sign-hint helper text shown beneath the input
     * (e.g., "positive = income, negative = expense" for misc). */
    action: LedgerInvestmentAction;
    error?: string | null;
    /** The save is in flight: the field keeps focus and is read-only (pending.ts). */
    pending?: boolean;
    /** The Review editor's rounded-at-import note (ADR-0114 D15, as board RV-2 has it for
     *  the bank editor): the field's description, muted, under it. */
    note?: string | null;
}

/**
 * Signed amount input. Used by dividend_cash (positive — dividend
 * value), transfer (sign = direction), and misc (sign = income vs
 * expense). The sign hint comes from <c>AMOUNT_SIGN_HINT</c> in
 * <c>actionLayout</c> so the per-action wording lives next to the
 * matrix, not scattered through JSX.
 */
export function AmountField({
    value, onChange, currency, action, error, pending, note,
}: AmountFieldProps) {
    const inputId = useId();
    const sizeClass = useFieldSizeClass();
    const signHint = AMOUNT_SIGN_HINT[action];
    const errorId = `${inputId}-error`;
    const noteId = `${inputId}-note`;
    const describedBy = [error ? errorId : null, note ? noteId : null].filter((id) => id !== null).join(' ');

    return (
        <div className="flex min-w-0 flex-col gap-1 text-xs">
            <FieldLabel htmlFor={inputId}>Amount</FieldLabel>
            <MoneyInput
                id={inputId}
                currency={currency}
                value={value ?? ''}
                pending={pending}
                onValueChange={(text) => onChange(parseMoneyText(text))}
                className={cn(
                    'h-control-28px w-full rounded border bg-surface px-2 text-right font-mono text-xs tabular-nums',
                    sizeClass,
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                    'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                    error ? 'border-state-danger' : 'border-border',
                )}
                aria-invalid={!!error}
                aria-describedby={describedBy === '' ? undefined : describedBy}
            />
            {error ? (
                <span
                    id={errorId}
                    className="text-[0.6875rem] leading-tight text-state-danger"
                >
                    {error}
                </span>
            ) : signHint ? (
                <span className="text-[0.625rem] leading-tight text-text-subtle">
                    {signHint}
                </span>
            ) : null}
            {/* Wraps at the field's width rather than widening it: w-0 with min-w-full
                adds nothing to the field's own width. */}
            {note ? (
                <span id={noteId} className="w-0 min-w-full text-right text-[0.6875rem] text-text-muted">
                    {note}
                </span>
            ) : null}
        </div>
    );
}
