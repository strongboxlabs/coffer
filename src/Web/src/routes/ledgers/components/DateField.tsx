import { useFieldSizeClass } from '@/components/ui/fieldSize';
import { pendingTextProps } from '@/components/ui/pending';
import { cn } from '@/lib/cn';
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
    /** The request this field belongs to is in flight: it keeps focus, is
     *  read-only, and its shortcuts do nothing (pending.ts). */
    pending?: boolean;
    autoFocus?: boolean;
    id?: string;
    /** Shown when the field is empty — used by tax date to say "same as posted". */
    placeholder?: string;
    /** Appended to the shortcut hint in the tooltip. */
    hint?: string;
    /**
     * Render the input alone. For a layout that puts captions and controls in
     * separate grid rows, so a caption that wraps cannot push its own input
     * out of line with its neighbours'. The caller then renders the caption as
     * a `FieldLabel htmlFor` this field's `id`.
     */
    bare?: boolean;
}

export function DateField({
    label,
    value,
    onChange,
    disabled,
    pending,
    autoFocus,
    id,
    placeholder,
    hint,
    bare,
}: DateFieldProps) {
    const sizeClass = useFieldSizeClass();
    const input = (
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
                // Pending: read-only, and no shortcut changes the date either.
                if (pending) return;
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
            // px-1, not the px-2 every other input uses. A native date control
            // renders its own segments and spinner inside the box, and at
            // px-2 this column is narrow enough that the year clipped to
            // three digits. The browser places the segments itself, so the
            // 4px buys legibility without costing an alignment that was
            // never the browser's to honour anyway.
            className={cn(
                'h-control-28px w-full rounded border border-border bg-surface px-1 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                sizeClass,
            )}
            {...pendingTextProps<HTMLInputElement>(pending)}
        />
    );
    if (bare === true) return input;
    return (
        <label className="flex min-w-0 flex-col gap-1">
            <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                {label}
            </span>
            {input}
        </label>
    );
}
