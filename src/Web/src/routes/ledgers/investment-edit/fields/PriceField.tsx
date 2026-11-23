import { useId } from 'react';
import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { MoneyInput } from '@/components/ui/MoneyInput';
import { useFieldSizeClass } from '@/components/ui/fieldSize';
import { parseMoneyText } from './moneyText';

interface PriceFieldProps {
    /** Current value. Null = empty input. */
    value: number | null;
    onChange: (next: number | null) => void;
    /** The brokerage's currency, whose symbol the price shows: price × shares is the
     *  trade's amount, which is in the brokerage's currency (ADR-0114 D14). Null while
     *  the accounts load: no symbol. */
    currency: string | null;
    /** Validation error message to render below the input. Null
     * suppresses the message slot entirely. */
    error?: string | null;
    /** The save is in flight: the field keeps focus and is read-only (pending.ts). */
    pending?: boolean;
    autoFocus?: boolean;
}

/**
 * Per-share price input. Always non-negative (the editor pins the
 * sign per the action × matrix; we surface a positive-only input
 * here so the user can't accidentally type a sign). Validation in
 * `../validation.ts` enforces `price > 0` for actions that require
 * it; this field's local rule is "any non-negative number or null."
 *
 * A price is not money (ADR-0114 D15): it shows the currency's symbol but takes any
 * step and is never rounded to the currency's places, so a six-place unit price is
 * kept as typed.
 */
export function PriceField({
    value, onChange, currency, error, pending, autoFocus,
}: PriceFieldProps) {
    const inputId = useId();
    const sizeClass = useFieldSizeClass();
    return (
        <div className="flex min-w-0 flex-col gap-1 text-xs">
            <FieldLabel htmlFor={inputId}>Price</FieldLabel>
            <MoneyInput
                id={inputId}
                price
                currency={currency}
                min="0"
                value={value ?? ''}
                pending={pending}
                autoFocus={autoFocus}
                onValueChange={(text) => onChange(parseMoneyText(text))}
                className={cn(
                    'h-control-28px w-full rounded border bg-surface px-2 text-right font-mono text-xs tabular-nums',
                    sizeClass,
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                    'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
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
            ) : null}
        </div>
    );
}
