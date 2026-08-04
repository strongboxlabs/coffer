import { useId } from 'react';
import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';

interface PriceFieldProps {
    /** Current value. Null = empty input. */
    value: number | null;
    onChange: (next: number | null) => void;
    /** Validation error message to render below the input. Null
     * suppresses the message slot entirely. */
    error?: string | null;
    disabled?: boolean;
    autoFocus?: boolean;
}

/**
 * Per-share price input. Always non-negative (the editor pins the
 * sign per the action × matrix; we surface a positive-only input
 * here so the user can't accidentally type a sign). Validation in
 * `../validation.ts` enforces `price > 0` for actions that require
 * it; this field's local rule is "any non-negative number or null."
 */
export function PriceField({
    value, onChange, error, disabled, autoFocus,
}: PriceFieldProps) {
    const inputId = useId();
    return (
        <div className="flex min-w-0 flex-col gap-1 text-xs">
            <FieldLabel htmlFor={inputId}>Price</FieldLabel>
            <input
                id={inputId}
                type="number"
                step="0.01"
                min="0"
                inputMode="decimal"
                value={value ?? ''}
                placeholder="0.00"
                disabled={disabled}
                autoFocus={autoFocus}
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
            ) : null}
        </div>
    );
}
