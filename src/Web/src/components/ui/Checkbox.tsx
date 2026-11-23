import { forwardRef, type InputHTMLAttributes, type ReactNode } from 'react';

import { cn } from '@/lib/cn';

import { usePendingChoiceProps } from './pending';

// Checkbox — the standard styled checkbox + label (was a hand-rolled
// `<input type="checkbox" className="size-icon-md …">` in ~13 files, some bare/
// unstyled). One affordance, one look. Pass `label` for the common
// checkbox-with-text case, or use bare (no label) inline. A ref reaches the
// input, so a form that takes the checkbox away can hand its focus on
// (useFocusHandOff, ADR-0023 §L).

export interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
    label?: ReactNode;
    /** Extra classes on the wrapping label (when `label` is set). */
    wrapperClassName?: string;
    /**
     * The request this checkbox belongs to is in flight: it keeps focus and
     * refuses to change until it is not (pending.ts, ADR-0023 §L). `disabled`
     * is for a checkbox that is unavailable for another reason. Given (true or
     * false), a change that writes holds the checkbox, and its PendingScope,
     * from the change until what it started has settled.
     */
    pending?: boolean;
}

export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox({
    label, className, wrapperClassName, pending, onChange, onClick, onKeyDown, onMouseDown, ...props
}, ref) {
    const pendingChoiceProps = usePendingChoiceProps();
    const box = (
        <input
            ref={ref}
            type="checkbox"
            className={cn(
                'size-icon-md rounded border-border text-accent',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                // A pending checkbox (aria-disabled, pending.ts) looks as a disabled one does.
                'disabled:cursor-not-allowed disabled:opacity-50',
                'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                className,
            )}
            {...props}
            {...pendingChoiceProps(pending, { onChange, onClick, onKeyDown, onMouseDown })}
        />
    );
    if (label === undefined) return box;
    return (
        <label className={cn('flex items-center gap-2 text-sm', wrapperClassName)}>
            {box}
            <span>{label}</span>
        </label>
    );
});
