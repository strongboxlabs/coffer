import { shiftDateInputValue, todayInputValue } from '@/lib/dates';

/**
 * A register-editor date input with the keyboard shortcuts the register uses
 * everywhere: `t` today, `y` yesterday, `+`/`-` shift a day.
 *
 * Extracted from TxnRowEdit, which carried this input twice — byte-identical
 * between the simple and split layouts, keyboard handler included. Adding the tax
 * date would have made four copies, so it became a component first. Behaviour for
 * the posted date is unchanged; the tax-date field is the second caller.
 */
export interface DateFieldProps {
    label: string;
    /** `YYYY-MM-DD`, or empty string for "not set". */
    value: string;
    onChange: (next: string) => void;
    disabled?: boolean;
    autoFocus?: boolean;
    id?: string;
    /** Shown when the field is empty — used by tax date to say "same as posted". */
    placeholder?: string;
    /** Appended to the shortcut hint in the tooltip. */
    hint?: string;
}

export function DateField({
    label,
    value,
    onChange,
    disabled,
    autoFocus,
    id,
    placeholder,
    hint,
}: DateFieldProps) {
    return (
        <label className="flex min-w-0 flex-col gap-1">
            <span className="text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted">
                {label}
            </span>
            <input
                id={id}
                type="date"
                value={value}
                disabled={disabled}
                autoFocus={autoFocus}
                aria-label={label}
                placeholder={placeholder}
                title={`${label} — keys: t today, y yesterday, +/- shift by day${hint ? `. ${hint}` : ''}`}
                onChange={(e) => onChange(e.target.value)}
                onKeyDown={(e) => {
                    if (e.key === 't' || e.key === 'T') {
                        e.preventDefault();
                        onChange(todayInputValue());
                    } else if (e.key === 'y' || e.key === 'Y') {
                        e.preventDefault();
                        onChange(shiftDateInputValue(todayInputValue(), -1));
                    } else if (e.key === '+' || e.key === '=') {
                        e.preventDefault();
                        onChange(shiftDateInputValue(value, 1));
                    } else if (e.key === '-' || e.key === '_') {
                        e.preventDefault();
                        onChange(shiftDateInputValue(value, -1));
                    }
                }}
                className="h-7 w-full rounded border border-border bg-surface px-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            />
        </label>
    );
}
